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

public sealed record LineBody(Guid? ItemId, decimal? Quantity, decimal? UnitPrice, string? SupplierLotCode = null, string? ActualSource = null);

/// <summary>INP-03 request body; every field can be left out (null) to check the validation.</summary>
public sealed record ReceiptBody
{
    public Guid? SupplierId { get; init; }
    public Guid? ZoneId { get; init; }
    public Guid? RaftId { get; init; }
    public DateOnly? ReceiptDate { get; init; } = PurchasingTestKit.Today;
    public decimal? DiscountAmount { get; init; }
    public decimal? AmountPaidNow { get; init; }
    public string? PaidNowMethod { get; init; }
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

public sealed record PaymentBody
{
    public Guid? SupplierId { get; init; }
    public Guid? ReceiptId { get; init; }
    public decimal? Amount { get; init; }
    public DateOnly? PaymentDate { get; init; } = PurchasingTestKit.Today;
    public string? Method { get; init; } = "BankTransfer";
    public string? ReferenceCode { get; init; }
    public string? DeduplicationKey { get; init; }
}

/// <summary>A zone with one In Use raft, a supplier and one item per group, created by the Owner through the API.</summary>
public sealed record PurchasingWorld(HttpClient Owner, FarmingZone Zone, Raft Raft, Guid SupplierId, Guid Seed, Guid Material, Guid Other)
{
    /// <summary>A body for this world's zone and supplier.</summary>
    public ReceiptBody Body(params LineBody[] lines) => new() { SupplierId = SupplierId, ZoneId = Zone.Id, Lines = lines };
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
            await owner.CreateItemAsync("Seed", "kg"), await owner.CreateItemAsync("RaftMaterial", "piece"), await owner.CreateItemAsync("Other", "bag"));
    }

    public static async Task<Guid> CreateSupplierAsync(this HttpClient client, string type = "Seed", string? phone = null, string? name = null)
    {
        var response = await client.PostAsJsonAsync("/api/suppliers", new { name = name ?? $"Supplier {NewCode()}", supplierType = type, phoneNumber = phone ?? NewPhone() });
        return (await response.ReadAsync(HttpStatusCode.Created)).GetProperty("id").GetGuid();
    }

    public static async Task<Guid> CreateItemAsync(this HttpClient owner, string group, string unit = "kg", string? code = null)
    {
        var response = await owner.PostAsJsonAsync("/api/farming-input-items", new { code = code ?? NewCode(), name = $"{group} item", group, unit });
        return (await response.ReadAsync(HttpStatusCode.Created)).GetProperty("id").GetGuid();
    }

    public static Task<HttpResponseMessage> PostReceiptAsync(this HttpClient client, ReceiptBody body) =>
        client.PostAsJsonAsync("/api/purchase-receipts", body);

    public static async Task<JsonElement> CreateReceiptAsync(this HttpClient client, ReceiptBody body) =>
        await (await client.PostReceiptAsync(body)).ReadAsync(HttpStatusCode.Created);

    /// <summary>A Completed receipt of the world with the given lines (a raft when a raft material is bought).</summary>
    public static Task<JsonElement> CompletedAsync(this PurchasingWorld world, decimal discount, decimal paidNow, params LineBody[] lines) =>
        world.Owner.CreateReceiptAsync(world.Body(lines) with
        {
            RaftId = lines.Any(l => l.ItemId == world.Material) ? world.Raft.Id : null,
            DiscountAmount = discount, AmountPaidNow = paidNow, PaidNowMethod = paidNow > 0 ? "Cash" : null, Complete = true,
        });

    public static Task<HttpResponseMessage> PostReturnAsync(this HttpClient client, Guid receiptId, ReturnBody body) =>
        client.PostAsJsonAsync($"/api/purchase-receipts/{receiptId}/returns", body);

    public static Task<HttpResponseMessage> PostPaymentAsync(this HttpClient client, PaymentBody body) =>
        client.PostAsJsonAsync("/api/supplier-payments", body);

    public static Task<HttpResponseMessage> CancelAsync(this HttpClient client, Guid receiptId, string? reason, bool? cancelPayments) =>
        client.PostAsJsonAsync($"/api/purchase-receipts/{receiptId}/cancel", new { reason, cancelPayments });

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

    /// <summary>Stocks <paramref name="quantity"/> of the seed lot of a receipt line, as CULT-02 will (it does not exist yet).</summary>
    public static async Task StockAsync(this AuthApiFactory factory, Guid raftId, Guid receiptLineId, decimal? quantity)
    {
        var lotId = await factory.QueryAsync(db => db.SeedReceiptLots.Where(l => l.PurchaseReceiptLineId == receiptLineId).Select(l => l.Id).SingleAsync());
        var cycle = new CultivationCycle { RaftId = raftId, OpenedAt = DateTimeOffset.UtcNow };
        await factory.UseDbAsync(db =>
        {
            db.CultivationCycles.Add(cycle);
            db.SeedingEvents.Add(new SeedingEvent
            {
                CycleId = cycle.Id, SeedLotId = lotId, SeedingDate = Today, SeedQuantity = quantity, RecordedByAccountId = db.Accounts.First().Id,
            });
        });
    }

    public static Task<List<string>> AuditActionsAsync(this AuthApiFactory factory, Guid recordId) =>
        factory.QueryAsync(db => db.AuditEvents.Where(e => e.RecordId == recordId).OrderBy(e => e.OccurredAt).Select(e => e.Action).ToListAsync());
}
