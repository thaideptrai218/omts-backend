using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using OMTS.Api.IntegrationTests.Infrastructure;
using OMTS.Application.Common;
using OMTS.Application.Features.Authorization;
using OMTS.Domain.Entities.Cultivation;
using OMTS.Domain.Entities.Farming;
using OMTS.Domain.Entities.Identity;
using OMTS.Domain.Enums;
using static OMTS.Api.IntegrationTests.Infrastructure.AcsTestKit;

namespace OMTS.Api.IntegrationTests.Purchasing;

public sealed record LineBody(Guid? ItemId, decimal? Quantity, decimal? UnitPrice, string? SupplierLotCode = null, Guid? RaftId = null);

/// <summary>PURCHASE-01 request body; every field can be left out (null) to check the validation.</summary>
public sealed record ReceiptBody
{
    public Guid? SupplierId { get; init; }
    public DateOnly? ReceiptDate { get; init; } = PurchasingTestKit.Today;
    public string? Notes { get; init; }
    public IReadOnlyList<LineBody>? Lines { get; init; }
    public bool Complete { get; init; }
    public string? RowVersion { get; init; }
}

public sealed record ReturnLineBody(Guid? ReceiptLineId, decimal? Quantity);

public sealed record ReturnBody
{
    public DateOnly? ReturnDate { get; init; } = PurchasingTestKit.Today;
    public string? Reason { get; init; } = "Weak seed";
    public decimal? RefundAmount { get; init; }
    public string? RefundMethod { get; init; }
    public decimal? DebtReductionAmount { get; init; }
    public IReadOnlyList<ReturnLineBody>? Lines { get; init; }
}

/// <summary>PURCHASE-05 request body: a cash payment dated today unless the test says otherwise.</summary>
public sealed record PaymentBody
{
    public Guid? SupplierId { get; init; }
    public string? Kind { get; init; } = "Payment";
    public decimal? Amount { get; init; }
    public DateOnly? PaymentDate { get; init; } = PurchasingTestKit.Today;
    public string? Method { get; init; } = "Cash";
    public string? BankReference { get; init; }
    public string? Note { get; init; }
    public bool ConfirmAdvance { get; init; }
    public bool ConfirmDuplicateBankReference { get; init; }
    public string? DeduplicationKey { get; init; }
}

/// <summary>A zone with one In Use raft, a supplier and one item per group, created by the Owner through the API.</summary>
public sealed record PurchasingWorld(HttpClient Owner, FarmingZone Zone, Raft Raft, Guid SupplierId, Guid Seed, Guid Material, Guid Other)
{
    /// <summary>A body for this world's supplier (receipts have no zone).</summary>
    public ReceiptBody Body(params LineBody[] lines) => new() { SupplierId = SupplierId, Lines = lines };

    /// <summary>A Material line bought for the world's raft (it counts in the raft material cost, BR-FARM-05).</summary>
    public LineBody MaterialForRaft(decimal quantity, decimal unitPrice) => new(Material, quantity, unitPrice, RaftId: Raft.Id);
}

/// <summary>Shared INP-01…INP-07 helpers: accounts with purchasing grants, catalogue rows and request bodies.</summary>
public static class PurchasingTestKit
{
    /// <summary>Today in the business time zone (UTC+7), as the server computes it.</summary>
    public static DateOnly Today => BusinessClock.ToBusinessDate(DateTimeOffset.UtcNow);

    public static async Task<HttpClient> PurchasingOwnerAsync(this AuthApiFactory factory)
    {
        await factory.EnsureCooperativeAsync();
        return await factory.LoginAsync((await factory.AddAccountAsync(NewCode(), Password)).Username);
    }

    /// <summary>Logged-in staff with one pair of the fixed <paramref name="role"/> in <paramref name="zone"/> (All zones when null).</summary>
    public static async Task<HttpClient> PurchasingStaffAsync(this AuthApiFactory factory, FarmingZone? zone, StaffRole role)
    {
        var staff = await factory.AddAccountAsync(NewCode(), Password, SubjectType.Staff);
        await factory.GrantAsync(staff, role, zone);
        return await factory.LoginAsync(staff.Username);
    }

    /// <summary>A Zone Manager of <paramref name="zone"/>: suppliers and every purchase receipt action there.</summary>
    public static Task<HttpClient> ZoneManagerAsync(this AuthApiFactory factory, FarmingZone zone) =>
        factory.PurchasingStaffAsync(zone, StaffRole.ZoneManager);

    /// <summary>
    /// An Accountant, at All zones unless <paramref name="zone"/> is given: supplier payments and debts (records with no
    /// zone, so any pair works), and View of suppliers and purchase receipts.
    /// </summary>
    public static Task<HttpClient> AccountantAsync(this AuthApiFactory factory, FarmingZone? zone = null) =>
        factory.PurchasingStaffAsync(zone, StaffRole.Accountant);

    public static async Task<(FarmingZone Zone, Raft Raft)> ZoneWithRaftAsync(this AuthApiFactory factory)
    {
        var zone = await factory.AddZoneAsync(NewCode());
        var raft = new Raft { ZoneId = zone.Id, Code = NewCode(), Size = "40 m x 20 m" };
        await factory.UseDbAsync(db => db.Rafts.Add(raft));
        return (zone, raft);
    }

    public static async Task<PurchasingWorld> WorldAsync(this AuthApiFactory factory, HttpClient? owner = null)
    {
        owner ??= await factory.PurchasingOwnerAsync();
        var (zone, raft) = await factory.ZoneWithRaftAsync();
        return new PurchasingWorld(
            owner, zone, raft, await owner.CreateSupplierAsync(),
            await owner.CreateItemAsync("Seed", "kg"), await owner.CreateItemAsync("Material", "piece"), await owner.CreateItemAsync("Other", "bag"));
    }

    public static async Task<Guid> CreateSupplierAsync(this HttpClient client, string type = "Seed", string? phone = null, string? name = null)
    {
        var response = await client.PostAsJsonAsync("/api/suppliers", new { name = name ?? $"Supplier {NewCode()}", supplierType = type, phoneNumber = phone ?? NewPhone() });
        return (await response.ReadAsync(HttpStatusCode.Created)).GetProperty("id").GetGuid();
    }

    /// <summary>
    /// An item named "{group} item {code}" (names are unique, BR-PURCHASE-10). Every Seed item of one database shares a
    /// unit, so the tests use "kg" for seed.
    /// </summary>
    public static async Task<Guid> CreateItemAsync(this HttpClient owner, string group, string unit = "kg", string? code = null)
    {
        code ??= NewCode();
        var response = await owner.PostAsJsonAsync("/api/farming-input-items", new { code, name = $"{group} item {code}", group, unit });
        return (await response.ReadAsync(HttpStatusCode.Created)).GetProperty("id").GetGuid();
    }

    public static Task<HttpResponseMessage> PostReceiptAsync(this HttpClient client, ReceiptBody body) =>
        client.PostAsJsonAsync("/api/purchase-receipts", body);

    public static async Task<JsonElement> CreateReceiptAsync(this HttpClient client, ReceiptBody body) =>
        await (await client.PostReceiptAsync(body)).ReadAsync(HttpStatusCode.Created);

    /// <summary>A Completed receipt of the world's supplier with the given lines, recorded by the Owner.</summary>
    public static Task<JsonElement> CompletedAsync(this PurchasingWorld world, params LineBody[] lines) =>
        world.Owner.CreateReceiptAsync(world.Body(lines) with { Complete = true });

    /// <summary>A Completed receipt, then a payment of <paramref name="paid"/> to its supplier (payments are not linked to receipts, BR-PURCHASE-04).</summary>
    public static async Task<JsonElement> PaidAsync(this PurchasingWorld world, decimal paid, params LineBody[] lines)
    {
        var receipt = await world.CompletedAsync(lines);
        await world.PayAsync(paid);
        return receipt;
    }

    /// <summary>A payment to the world's supplier, recorded by the Owner, answering Yes to the advance warning.</summary>
    public static async Task<JsonElement> PayAsync(this PurchasingWorld world, decimal amount, DateOnly? date = null, string kind = "Payment") =>
        await (await world.Owner.PostPaymentAsync(new PaymentBody
        {
            SupplierId = world.SupplierId, Kind = kind, Amount = amount, PaymentDate = date ?? Today, ConfirmAdvance = true,
        })).ReadAsync(HttpStatusCode.Created);

    /// <summary>The supplier debt today, as PURCHASE-04 shows it.</summary>
    public static async Task<decimal> DebtAsync(this PurchasingWorld world) =>
        (await world.Owner.GetFromJsonAsync<JsonElement>($"/api/supplier-debts/{world.SupplierId}")).Money("debtToday");

    public static Task<HttpResponseMessage> PostReturnAsync(this HttpClient client, Guid receiptId, ReturnBody body) =>
        client.PostAsJsonAsync($"/api/purchase-receipts/{receiptId}/returns", body);

    public static Task<HttpResponseMessage> PostPaymentAsync(this HttpClient client, PaymentBody body) =>
        client.PostAsJsonAsync("/api/supplier-payments", body);

    public static Task<HttpResponseMessage> CancelPaymentAsync(this HttpClient client, Guid paymentId, string? reason, string? rowVersion = null) =>
        client.PostAsJsonAsync($"/api/supplier-payments/{paymentId}/cancel", new { reason, rowVersion });

    public static Task<HttpResponseMessage> CancelAsync(this HttpClient client, Guid receiptId, string? reason) =>
        client.PostAsJsonAsync($"/api/purchase-receipts/{receiptId}/cancel", new { reason });

    public static Task<HttpResponseMessage> CompleteAsync(this HttpClient client, Guid receiptId) =>
        client.PostAsync($"/api/purchase-receipts/{receiptId}/complete", null);

    public static Task<HttpResponseMessage> EditAsync(this HttpClient client, JsonElement draft, ReceiptBody body) =>
        client.PutAsJsonAsync($"/api/purchase-receipts/{draft.Id()}", body with { RowVersion = draft.GetProperty("rowVersion").GetString() });

    /// <summary>The seed lot code (GBR-04) of the line of <paramref name="itemId"/>, or null.</summary>
    public static string? SeedLotCodeOf(this JsonElement receipt, Guid itemId) => receipt.LineViewOf(itemId).GetProperty("seedLotCode").GetString();

    /// <summary>The id of the receipt line of <paramref name="itemId"/>.</summary>
    public static Guid LineOf(this JsonElement receipt, Guid itemId) =>
        receipt.GetProperty("lines").EnumerateArray().First(l => l.GetProperty("itemId").GetGuid() == itemId).GetProperty("id").GetGuid();

    public static JsonElement LineViewOf(this JsonElement receipt, Guid itemId) =>
        receipt.GetProperty("lines").EnumerateArray().First(l => l.GetProperty("itemId").GetGuid() == itemId);

    public static decimal Money(this JsonElement element, string property) => element.GetProperty(property).GetDecimal();

    public static Guid Id(this JsonElement element) => element.GetProperty("id").GetGuid();

    /// <summary>Asserts the status (showing the body when it differs) and returns the JSON body.</summary>
    public static async Task<JsonElement> ReadAsync(this HttpResponseMessage response, HttpStatusCode expected)
    {
        var text = await response.Content.ReadAsStringAsync();
        Assert.True(response.StatusCode == expected, $"Expected {(int)expected}, got {(int)response.StatusCode}: {text}");
        return text.Length == 0 ? default : JsonDocument.Parse(text).RootElement.Clone();
    }

    /// <summary>Asserts a problem response with the status and MSG code, and returns its body.</summary>
    public static async Task<JsonElement> ProblemAsync(this HttpResponseMessage response, HttpStatusCode status, string code)
    {
        var body = await response.ReadAsync(status);
        Assert.Equal(code, body.GetProperty("code").GetString());
        return body;
    }

    /// <summary>The <c>field</c> of an MSG01/MSG45/MSG59 problem.</summary>
    public static async Task<string?> FieldAsync(this HttpResponseMessage response, HttpStatusCode status, string code) =>
        (await response.ProblemAsync(status, code)).TryGetProperty("field", out var field) ? field.GetString() : null;

    /// <summary>
    /// Seeds <paramref name="quantity"/> (null: a session without a quantity) of the seed lot that is the receipt line
    /// (GBR-04), as CYCLE-02 will (it does not exist yet).
    /// </summary>
    public static async Task SeedAsync(this AuthApiFactory factory, Guid raftId, Guid receiptLineId, decimal? quantity)
    {
        var cycle = new CultivationCycle { RaftId = raftId, OpenedAt = DateTimeOffset.UtcNow };
        await factory.UseDbAsync(db =>
        {
            db.CultivationCycles.Add(cycle);
            db.SeedingEvents.Add(new SeedingEvent
            {
                CycleId = cycle.Id, SeedLotId = receiptLineId, SeedingDate = Today, SeedQuantity = quantity, RecordedByAccountId = db.Accounts.First().Id,
            });
        });
    }

    public static Task<List<string>> AuditActionsAsync(this AuthApiFactory factory, Guid recordId) =>
        factory.QueryAsync(db => db.AuditEvents.Where(e => e.RecordId == recordId).OrderBy(e => e.OccurredAt).Select(e => e.Action).ToListAsync());
}
