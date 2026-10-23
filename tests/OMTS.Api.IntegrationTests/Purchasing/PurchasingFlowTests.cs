using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using OMTS.Api.IntegrationTests.Infrastructure;
using OMTS.Application.Features.Authorization;
using OMTS.Domain.Constants;
using OMTS.Domain.Entities.Cultivation;
using OMTS.Domain.Entities.Farming;
using OMTS.Domain.Enums;
using static OMTS.Api.IntegrationTests.Infrastructure.AcsTestKit;

namespace OMTS.Api.IntegrationTests.Purchasing;

/// <summary>INP-01…INP-07 main flows: supplier, items, receipt → seed lots and payment, return, payment, cancel/copy, debt and list.</summary>
public class PurchasingFlowTests(AuthApiFactory factory) : IClassFixture<AuthApiFactory>
{
    [Fact]
    public async Task Completed_receipt_creates_seed_lots_and_the_paid_now_payment_and_the_debt_follows_every_step()
    {
        var owner = await OwnerAsync();
        var (zone, raft) = await ZoneWithRaftAsync();
        var supplierId = await CreateSupplierAsync(owner);
        var seedA = await CreateItemAsync(owner, "Seed");
        var seedB = await CreateItemAsync(owner, "Seed");
        var floats = await CreateItemAsync(owner, "RaftMaterial");

        // Subtotal 2 × 1,000,000 + 500,000 = 2,500,000; discount 100,000; paid now 400,000.
        var created = await owner.PostAsJsonAsync("/api/purchase-receipts", new
        {
            supplierId, zoneId = zone.Id, raftId = raft.Id, receiptDate = Today(), discountAmount = 100_000, amountPaidNow = 400_000, paidNowMethod = "Cash",
            lines = new object[]
            {
                new { itemId = seedA, quantity = 100, unitPrice = 10_000, supplierLotCode = "SUP-1" },
                new { itemId = seedB, quantity = 50, unitPrice = 20_000 },
                new { itemId = floats, quantity = 50, unitPrice = 10_000 },
            },
            complete = true,
        });
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var receipt = await created.Content.ReadFromJsonAsync<JsonElement>();
        var receiptId = receipt.GetProperty("id").GetGuid();
        Assert.Equal("Completed", receipt.GetProperty("status").GetString());
        Assert.Equal(2_400_000, receipt.GetProperty("payable").GetDecimal());
        Assert.Equal(2_000_000, receipt.GetProperty("outstanding").GetDecimal());
        Assert.Equal(2, await factory.QueryAsync(db => db.SeedReceiptLots.CountAsync(l => l.PurchaseReceiptLine.ReceiptId == receiptId)));
        Assert.Equal(1, await factory.QueryAsync(db => db.SupplierPayments.CountAsync(p => p.ReceiptId == receiptId)));

        // Raft material cost: floats 500,000 less 96 % of the discount share → 480,000.
        var raftView = await owner.GetFromJsonAsync<JsonElement>($"/api/rafts/{raft.Id}");
        Assert.Equal(480_000, raftView.GetProperty("materialCost").GetDecimal());

        // A completed receipt is never edited.
        var edit = await owner.PutAsJsonAsync($"/api/purchase-receipts/{receiptId}", new { supplierId, zoneId = zone.Id, lines = Array.Empty<object>() });
        Assert.Equal("MSG61", await edit.ErrorCodeAsync());

        // Return 20 of seed A (stock 0): value 20 × 10,000 × 0.96 = 192,000, half refund half debt reduction.
        var seedLine = receipt.GetProperty("lines").EnumerateArray().First(l => l.GetProperty("itemId").GetGuid() == seedA).GetProperty("id").GetGuid();
        var tooMuch = await owner.PostAsJsonAsync($"/api/purchase-receipts/{receiptId}/returns", ReturnBody(seedLine, 101, 0, 0));
        Assert.Equal("MSG45", await tooMuch.ErrorCodeAsync());
        var returned = await owner.PostAsJsonAsync($"/api/purchase-receipts/{receiptId}/returns", ReturnBody(seedLine, 20, 96_000, 96_000));
        Assert.Equal(HttpStatusCode.Created, returned.StatusCode);
        var afterReturn = await returned.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(192_000, afterReturn.GetProperty("returned").GetDecimal());
        Assert.Equal(2_400_000 - 192_000 - (400_000 - 96_000), afterReturn.GetProperty("outstanding").GetDecimal());

        // Payment: never above the outstanding; a repeated key is idempotent.
        var outstanding = afterReturn.GetProperty("outstanding").GetDecimal();
        var over = await owner.PostAsJsonAsync("/api/supplier-payments", PaymentBody(supplierId, receiptId, outstanding + 1000, "k-over"));
        Assert.Equal("MSG45", await over.ErrorCodeAsync());
        var paid = await owner.PostAsJsonAsync("/api/supplier-payments", PaymentBody(supplierId, receiptId, 1_000_000, "k-1"));
        var again = await owner.PostAsJsonAsync("/api/supplier-payments", PaymentBody(supplierId, receiptId, 1_000_000, "k-1"));
        Assert.Equal((await paid.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid(), (await again.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid());

        var debt = await owner.GetFromJsonAsync<JsonElement>($"/api/suppliers/{supplierId}/debt");
        Assert.Equal(outstanding - 1_000_000, debt.GetProperty("totals").GetProperty("outstanding").GetDecimal());

        // A receipt with a purchase return cannot be cancelled.
        var cancel = await owner.PostAsJsonAsync($"/api/purchase-receipts/{receiptId}/cancel", new { reason = "Wrong", cancelPayments = true });
        Assert.Equal("MSG61", await cancel.ErrorCodeAsync());
    }

    [Theory]
    [InlineData(true, 0)]
    [InlineData(false, -300_000)]
    public async Task Cancel_with_each_payment_choice_gives_the_expected_supplier_debt(bool cancelPayments, int expectedDebt)
    {
        var owner = await OwnerAsync();
        var (zone, _) = await ZoneWithRaftAsync();
        var supplierId = await CreateSupplierAsync(owner);
        var item = await CreateItemAsync(owner, "Other");
        var receiptId = await CompletedReceiptAsync(owner, supplierId, zone.Id, item, paidNow: 300_000);

        var noChoice = await owner.PostAsJsonAsync($"/api/purchase-receipts/{receiptId}/cancel", new { reason = "Entered twice" });
        Assert.Equal("MSG01", await noChoice.ErrorCodeAsync());
        var cancelled = await owner.PostAsJsonAsync($"/api/purchase-receipts/{receiptId}/cancel", new { reason = "Entered twice", cancelPayments });
        Assert.Equal(HttpStatusCode.OK, cancelled.StatusCode);

        var debt = await owner.GetFromJsonAsync<JsonElement>($"/api/suppliers/{supplierId}/debt");
        Assert.Equal(expectedDebt, debt.GetProperty("totals").GetProperty("outstanding").GetDecimal());

        var copy = await owner.PostAsync($"/api/purchase-receipts/{receiptId}/copy", null);
        Assert.Equal(HttpStatusCode.Created, copy.StatusCode);
        var draft = await copy.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("Draft", draft.GetProperty("status").GetString());
        Assert.Equal(receiptId, draft.GetProperty("copiedFromReceiptId").GetGuid());
        Assert.Empty(draft.GetProperty("payments").EnumerateArray());
    }

    [Fact]
    public async Task Stocked_seed_lot_blocks_cancel_and_a_zone_manager_never_sees_another_zone()
    {
        var owner = await OwnerAsync();
        var (zone, raft) = await ZoneWithRaftAsync();
        var (otherZone, _) = await ZoneWithRaftAsync();
        var supplierId = await CreateSupplierAsync(owner);
        var seed = await CreateItemAsync(owner, "Seed");
        var receiptId = await CompletedReceiptAsync(owner, supplierId, zone.Id, seed, paidNow: 0);
        var lotId = await factory.QueryAsync(db => db.SeedReceiptLots.Where(l => l.PurchaseReceiptLine.ReceiptId == receiptId).Select(l => l.Id).SingleAsync());
        var cycle = new CultivationCycle { RaftId = raft.Id, OpenedAt = DateTimeOffset.UtcNow };
        await factory.UseDbAsync(db =>
        {
            db.CultivationCycles.Add(cycle);
            db.SeedingEvents.Add(new SeedingEvent { CycleId = cycle.Id, SeedLotId = lotId, SeedingDate = DateOnly.Parse(Today()), SeedQuantity = 5, RecordedByAccountId = db.Accounts.First().Id });
        });

        var cancel = await owner.PostAsJsonAsync($"/api/purchase-receipts/{receiptId}/cancel", new { reason = "Wrong" });
        Assert.Equal("MSG61", await cancel.ErrorCodeAsync());

        var manager = await ManagerOfAsync(otherZone);
        Assert.Equal(HttpStatusCode.Forbidden, (await manager.GetAsync($"/api/purchase-receipts/{receiptId}")).StatusCode);
        var list = await manager.GetFromJsonAsync<JsonElement>("/api/purchase-receipts");
        Assert.DoesNotContain(list.GetProperty("page").GetProperty("items").EnumerateArray(), r => r.GetProperty("id").GetGuid() == receiptId);
        var create = await manager.PostAsJsonAsync("/api/purchase-receipts", new { supplierId, zoneId = zone.Id, lines = Array.Empty<object>() });
        Assert.Equal(HttpStatusCode.Forbidden, create.StatusCode);

        var export = await owner.GetAsync("/api/purchase-receipts/export");
        Assert.Equal(HttpStatusCode.OK, export.StatusCode);
        Assert.Equal("application/vnd.openxmlformats-officedocument.spreadsheetml.sheet", export.Content.Headers.ContentType?.MediaType);
    }

    [Fact]
    public async Task Items_are_owner_only_unique_and_keep_code_group_and_unit_once_used()
    {
        var owner = await OwnerAsync();
        var (zone, _) = await ZoneWithRaftAsync();
        var code = NewCode();
        var created = await owner.PostAsJsonAsync("/api/farming-input-items", new { code, name = "Float 40 L", group = "RaftMaterial", unit = "piece" });
        var item = await created.Content.ReadFromJsonAsync<JsonElement>();
        var duplicate = await owner.PostAsJsonAsync("/api/farming-input-items", new { code = code.ToUpperInvariant(), name = "Again", group = "RaftMaterial", unit = "piece" });
        Assert.Equal("MSG03", await duplicate.ErrorCodeAsync());

        var supplierId = await CreateSupplierAsync(owner);
        await CompletedReceiptAsync(owner, supplierId, zone.Id, item.GetProperty("id").GetGuid(), paidNow: 0, raftNeeded: true);
        var changeUnit = await owner.PutAsJsonAsync($"/api/farming-input-items/{item.GetProperty("id").GetGuid()}", new
        {
            code, name = "Float 40 L", group = "RaftMaterial", unit = "kg", rowVersion = item.GetProperty("rowVersion").GetString(),
        });
        Assert.Equal("MSG01", await changeUnit.ErrorCodeAsync());

        var manager = await ManagerOfAsync(zone);
        Assert.Equal(HttpStatusCode.Forbidden, (await manager.PostAsJsonAsync("/api/farming-input-items", new { code = NewCode(), name = "X", group = "Other", unit = "kg" })).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await manager.GetAsync("/api/farming-input-items")).StatusCode);

        var duplicatePhone = await owner.PostAsJsonAsync("/api/suppliers", new { name = "Other", supplierType = "Seed", phoneNumber = await SupplierPhoneAsync(supplierId) });
        Assert.Equal("MSG03", await duplicatePhone.ErrorCodeAsync());
        Assert.Equal(supplierId, (await duplicatePhone.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("existingId").GetGuid());
    }

    private async Task<Guid> CompletedReceiptAsync(HttpClient owner, Guid supplierId, Guid zoneId, Guid itemId, decimal paidNow, bool raftNeeded = false)
    {
        Guid? raftId = raftNeeded ? await factory.QueryAsync(db => db.Rafts.Where(r => r.ZoneId == zoneId).Select(r => r.Id).FirstAsync()) : null;
        var response = await owner.PostAsJsonAsync("/api/purchase-receipts", new
        {
            supplierId, zoneId, raftId, receiptDate = Today(), amountPaidNow = paidNow, paidNowMethod = paidNow > 0 ? "Cash" : null,
            lines = new[] { new { itemId, quantity = 10, unitPrice = 100_000 } },
            complete = true,
        });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();
    }

    private async Task<HttpClient> OwnerAsync()
    {
        await factory.EnsureCooperativeAsync();
        return await factory.LoginAsync((await factory.AddAccountAsync(NewCode(), Password)).Username);
    }

    private async Task<HttpClient> ManagerOfAsync(FarmingZone zone)
    {
        var staff = await factory.AddAccountAsync(NewCode(), Password, SubjectType.Staff);
        var role = await factory.AddRoleAsync(NewCode(), (PermissionFunctions.PurchaseReceipts, true, true, true), (PermissionFunctions.Suppliers, true, true, true));
        await factory.GrantAsync(staff, role, zone);
        return await factory.LoginAsync(staff.Username);
    }

    private async Task<(FarmingZone Zone, Raft Raft)> ZoneWithRaftAsync()
    {
        var zone = await factory.AddZoneAsync(NewCode());
        var raft = new Raft { ZoneId = zone.Id, Code = NewCode(), Size = "40 m x 20 m" };
        await factory.UseDbAsync(db => db.Rafts.Add(raft));
        return (zone, raft);
    }

    private static async Task<Guid> CreateSupplierAsync(HttpClient owner)
    {
        var response = await owner.PostAsJsonAsync("/api/suppliers", new { name = $"Supplier {NewCode()}", supplierType = "Seed", phoneNumber = NewPhone() });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();
    }

    private static async Task<Guid> CreateItemAsync(HttpClient owner, string group)
    {
        var response = await owner.PostAsJsonAsync("/api/farming-input-items", new { code = NewCode(), name = $"{group} item", group, unit = "kg" });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();
    }

    private Task<string> SupplierPhoneAsync(Guid supplierId) =>
        factory.QueryAsync(db => db.Suppliers.Where(s => s.Id == supplierId).Select(s => s.PhoneNumber).SingleAsync());

    private static object ReturnBody(Guid lineId, decimal quantity, decimal refund, decimal debtReduction) => new
    {
        returnDate = Today(), reason = "Weak seed", refundAmount = refund, refundMethod = refund > 0 ? "Cash" : null, debtReductionAmount = debtReduction,
        lines = new[] { new { receiptLineId = lineId, quantity } },
    };

    private static object PaymentBody(Guid supplierId, Guid receiptId, decimal amount, string key) => new
    {
        supplierId, receiptId, amount, paymentDate = Today(), method = "BankTransfer", deduplicationKey = $"{key}-{receiptId:N}",
    };

    private static string Today() => DateOnly.FromDateTime(DateTimeOffset.UtcNow.ToOffset(TimeSpan.FromHours(7)).DateTime).ToString("yyyy-MM-dd");
}
