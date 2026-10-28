using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using OMTS.Api.IntegrationTests.Infrastructure;
using OMTS.Application.Features.Authorization;
using OMTS.Domain.Constants;

namespace OMTS.Api.IntegrationTests.Purchasing;

/// <summary>INP-04 cancel (reason, guards, payment choice) and copy, with their audit rows.</summary>
public class PurchaseReceiptCancelCopyTests(AuthApiFactory factory) : IClassFixture<AuthApiFactory>
{
    [Theory]
    [InlineData(null)]
    [InlineData("   ")]
    public async Task A_missing_reason_returns_MSG01_and_changes_nothing(string? reason)
    {
        var world = await factory.WorldAsync();
        var receipt = await world.CompletedAsync(0, 0, new LineBody(world.Other, 1, 10_000));
        Assert.Equal("Reason", await (await world.Owner.CancelAsync(receipt.Id(), reason, true)).FieldAsync(HttpStatusCode.BadRequest, "MSG-GEN-01"));
        var tooLong = await world.Owner.CancelAsync(receipt.Id(), new string('r', 1001), true);
        Assert.Equal("Reason", await tooLong.FieldAsync(HttpStatusCode.BadRequest, "MSG-GEN-01"));
        Assert.Equal(StatusIds.PurchaseReceipt.Completed, (await factory.QueryAsync(db => db.PurchaseReceipts.SingleAsync(r => r.Id == receipt.Id()))).StatusId);
    }

    [Fact]
    public async Task A_draft_is_always_cancelled_and_a_cancelled_receipt_cannot_be_cancelled_again()
    {
        var world = await factory.WorldAsync();
        var draft = await world.Owner.CreateReceiptAsync(world.Body(new LineBody(world.Other, 1, 10_000)) with { AmountPaidNow = 5000, PaidNowMethod = "Cash" });

        var cancelled = await (await world.Owner.CancelAsync(draft.Id(), "Not bought after all", null)).ReadAsync(HttpStatusCode.OK);
        Assert.Equal(("Cancelled", "Not bought after all"), (cancelled.GetProperty("status").GetString(), cancelled.GetProperty("cancelReason").GetString()));
        Assert.NotNull(cancelled.GetProperty("cancelledAt").GetString());

        var again = await world.Owner.CancelAsync(draft.Id(), "Again", true);
        var problem = await again.ProblemAsync(HttpStatusCode.Conflict, "MSG-GEN-09");
        Assert.EndsWith("is Cancelled and cannot be used for this action.", problem.GetProperty("detail").GetString());

        var audit = await factory.QueryAsync(db => db.AuditEvents.SingleAsync(e => e.RecordId == draft.Id() && e.Action == "PurchaseReceipt.Cancelled"));
        Assert.Equal(("Not bought after all", world.Zone.Id), (audit.Reason, audit.ZoneId));
        Assert.Contains("DRAFT", audit.Before);
        Assert.Contains("CANCELLED", audit.After);
    }

    [Fact]
    public async Task Cancelling_with_payments_cancelled_records_each_payment_status_change_with_the_reason()
    {
        var world = await factory.WorldAsync();
        var receipt = await world.CompletedAsync(0, 200_000, new LineBody(world.Other, 10, 100_000));
        var extra = await (await world.Owner.PostPaymentAsync(new PaymentBody { SupplierId = world.SupplierId, ReceiptId = receipt.Id(), Amount = 300_000 })).ReadAsync(HttpStatusCode.Created);

        var cancelled = await (await world.Owner.CancelAsync(receipt.Id(), "Wrong supplier", true)).ReadAsync(HttpStatusCode.OK);

        Assert.All(cancelled.GetProperty("payments").EnumerateArray(), p => Assert.Equal("Cancelled", p.GetProperty("status").GetString()));
        var payments = await factory.QueryAsync(db => db.SupplierPayments.Where(p => p.ReceiptId == receipt.Id()).ToListAsync());
        Assert.Equal(2, payments.Count);
        Assert.All(payments, p => Assert.Equal((StatusIds.SupplierPayment.Cancelled, "Wrong supplier"), (p.StatusId, p.CancelReason)));
        foreach (var payment in payments)
        {
            var change = await factory.QueryAsync(db => db.AuditEvents.SingleAsync(e => e.RecordId == payment.Id && e.Action == "SupplierPayment.StatusChanged"));
            Assert.Equal(("Wrong supplier", world.Zone.Id), (change.Reason, change.ZoneId));
        }

        Assert.Contains(payments, p => p.Id == extra.Id());
        var receiptAudit = await factory.QueryAsync(db => db.AuditEvents.SingleAsync(e => e.RecordId == receipt.Id() && e.Action == "PurchaseReceipt.Cancelled"));
        Assert.Contains("\"PaymentsCancelled\":true", receiptAudit.After);

        var debt = await world.Owner.GetFromJsonAsync<JsonElement>($"/api/suppliers/{world.SupplierId}/debt");
        Assert.Equal(0, debt.GetProperty("totals").Money("outstanding"));
        Assert.Empty(debt.GetProperty("receipts").EnumerateArray());
    }

    [Fact]
    public async Task Cancelling_with_payments_kept_detaches_them_as_an_unallocated_amount_with_audit()
    {
        var world = await factory.WorldAsync();
        var receipt = await world.CompletedAsync(0, 200_000, new LineBody(world.Other, 10, 100_000));
        var other = await world.CompletedAsync(0, 0, new LineBody(world.Other, 5, 100_000));

        var cancelled = await (await world.Owner.CancelAsync(receipt.Id(), "Entered twice", false)).ReadAsync(HttpStatusCode.OK);

        Assert.Empty(cancelled.GetProperty("payments").EnumerateArray());
        var payment = await factory.QueryAsync(db => db.SupplierPayments.SingleAsync(p => p.SupplierId == world.SupplierId));
        Assert.Null(payment.ReceiptId);
        Assert.Equal(StatusIds.SupplierPayment.Recorded, payment.StatusId);
        var detached = await factory.QueryAsync(db => db.AuditEvents.SingleAsync(e => e.RecordId == payment.Id && e.Action == "SupplierPayment.Detached"));
        Assert.Equal(("Entered twice", world.Zone.Id), (detached.Reason, detached.ZoneId));
        var receiptAudit = await factory.QueryAsync(db => db.AuditEvents.SingleAsync(e => e.RecordId == receipt.Id() && e.Action == "PurchaseReceipt.Cancelled"));
        Assert.Contains("\"PaymentsCancelled\":false", receiptAudit.After);

        // Supplier debt = outstanding of Completed receipts (500,000) − unallocated (200,000).
        var debt = await world.Owner.GetFromJsonAsync<JsonElement>($"/api/suppliers/{world.SupplierId}/debt");
        Assert.Equal((200_000m, 300_000m), (debt.GetProperty("totals").Money("unallocated"), debt.GetProperty("totals").Money("outstanding")));
        Assert.Equal([other.Id()], debt.GetProperty("receipts").EnumerateArray().Select(r => r.GetProperty("receiptId").GetGuid()));
    }

    [Fact]
    public async Task A_completed_receipt_without_payments_needs_no_payment_choice_and_leaves_costs()
    {
        var world = await factory.WorldAsync();
        var receipt = await world.CompletedAsync(0, 0, new LineBody(world.Material, 10, 50_000));
        Assert.Equal(500_000, (await world.Owner.GetFromJsonAsync<JsonElement>($"/api/rafts/{world.Raft.Id}")).Money("materialCost"));

        await (await world.Owner.CancelAsync(receipt.Id(), "Wrong raft", null)).ReadAsync(HttpStatusCode.OK);

        Assert.Equal(0, (await world.Owner.GetFromJsonAsync<JsonElement>($"/api/rafts/{world.Raft.Id}")).Money("materialCost"));
        var list = await world.Owner.GetFromJsonAsync<JsonElement>($"/api/purchase-receipts?supplierId={world.SupplierId}");
        Assert.Equal((1, 0, 0m), (list.GetProperty("totals").GetProperty("count").GetInt32(), list.GetProperty("totals").GetProperty("completedCount").GetInt32(), list.GetProperty("totals").Money("payable")));
    }

    [Fact]
    public async Task Seed_lots_of_a_cancelled_receipt_stay_for_history()
    {
        var world = await factory.WorldAsync();
        var receipt = await world.CompletedAsync(0, 0, new LineBody(world.Seed, 100, 1000));
        await (await world.Owner.CancelAsync(receipt.Id(), "Supplier sent another lot", null)).ReadAsync(HttpStatusCode.OK);
        var kept = await world.Owner.GetFromJsonAsync<JsonElement>($"/api/purchase-receipts/{receipt.Id()}");
        Assert.NotEqual(JsonValueKind.Null, kept.LineViewOf(world.Seed).GetProperty("seedLotId").ValueKind);
        Assert.Equal(1, await factory.QueryAsync(db => db.SeedReceiptLots.CountAsync(l => l.PurchaseReceiptLine.ReceiptId == receipt.Id())));
    }

    [Fact]
    public async Task A_seed_lot_seeded_without_a_quantity_blocks_cancel_too_naming_the_lot()
    {
        var world = await factory.WorldAsync();
        var receipt = await world.CompletedAsync(0, 0, new LineBody(world.Other, 1, 1000), new LineBody(world.Seed, 100, 1000));
        await factory.StockAsync(world.Raft.Id, receipt.LineOf(world.Seed), quantity: null);

        var problem = await (await world.Owner.CancelAsync(receipt.Id(), "Wrong", null)).ProblemAsync(HttpStatusCode.Conflict, "MSG-PURCHASE-09");
        var lotCode = receipt.LineViewOf(world.Seed).GetProperty("seedLotCode").GetString();
        var receiptCode = receipt.GetProperty("code").GetString();
        Assert.Equal(
            $"Receipt {receiptCode} cannot be cancelled: seed lot {lotCode} has already been stocked. The Cooperative Owner can correct it instead.",
            problem.GetProperty("detail").GetString());
        Assert.Equal("MSG61", problem.GetProperty("legacyCode").GetString());
    }

    [Fact]
    public async Task Cancel_needs_a_zone_manager_pair_in_the_receipt_zone()
    {
        var world = await factory.WorldAsync();
        var receipt = await world.CompletedAsync(0, 0, new LineBody(world.Other, 1, 10_000));
        var (otherZone, _) = await factory.ZoneWithRaftAsync();

        // The Accountant only views purchase receipts (§I.4.4 PURCHASE-03 R, PURCHASE-06 No).
        var accountant = await factory.AccountantAsync(world.Zone);
        await (await accountant.CancelAsync(receipt.Id(), "Wrong", null)).ProblemAsync(HttpStatusCode.Forbidden, "MSG-GEN-08");
        var otherManager = await factory.ZoneManagerAsync(otherZone);
        await (await otherManager.CancelAsync(receipt.Id(), "Wrong", null)).ProblemAsync(HttpStatusCode.Forbidden, "MSG-GEN-08");
        await (await otherManager.PostAsync($"/api/purchase-receipts/{receipt.Id()}/copy", null)).ProblemAsync(HttpStatusCode.Forbidden, "MSG-GEN-08");
        Assert.Equal(StatusIds.PurchaseReceipt.Completed, (await factory.QueryAsync(db => db.PurchaseReceipts.SingleAsync(r => r.Id == receipt.Id()))).StatusId);

        var manager = await factory.ZoneManagerAsync(world.Zone);
        await (await manager.CancelAsync(receipt.Id(), "Wrong", null)).ReadAsync(HttpStatusCode.OK);
    }

    // Done when: a copy is an independent Draft.
    [Fact]
    public async Task A_copy_is_an_independent_draft_with_the_same_data_and_no_payments_or_seed_lots()
    {
        var world = await factory.WorldAsync();
        var source = await world.Owner.CreateReceiptAsync(world.Body(
            new LineBody(world.Seed, 100, 10_000, "SUP-9", "Cát Bà"), new LineBody(world.Material, 20, 25_000)) with
        {
            RaftId = world.Raft.Id, DiscountAmount = 50_000, AmountPaidNow = 400_000, PaidNowMethod = "Cash", Notes = "Original", Complete = true,
        });

        // A Draft is edited, not copied.
        var draft = await world.Owner.CreateReceiptAsync(world.Body(new LineBody(world.Other, 1, 1000)));
        await (await world.Owner.PostAsync($"/api/purchase-receipts/{draft.Id()}/copy", null)).ProblemAsync(HttpStatusCode.Conflict, "MSG-GEN-09");

        await (await world.Owner.CancelAsync(source.Id(), "Wrong price", false)).ReadAsync(HttpStatusCode.OK);
        var copy = await (await world.Owner.PostAsync($"/api/purchase-receipts/{source.Id()}/copy", null)).ReadAsync(HttpStatusCode.Created);

        Assert.NotEqual(source.GetProperty("code").GetString(), copy.GetProperty("code").GetString());
        Assert.Equal(("Draft", source.Id()), (copy.GetProperty("status").GetString(), copy.GetProperty("copiedFromReceiptId").GetGuid()));
        Assert.Equal((world.SupplierId, world.Zone.Id, world.Raft.Id), (copy.GetProperty("supplier").Id(), copy.GetProperty("zone").Id(), copy.GetProperty("raft").Id()));
        Assert.Equal((50_000m, "Original", 0m), (copy.Money("discountAmount"), copy.GetProperty("notes").GetString(), copy.Money("amountPaidNow")));
        Assert.Equal(source.Money("subtotal"), copy.Money("subtotal"));
        var copiedSeed = copy.LineViewOf(world.Seed);
        Assert.Equal(("SUP-9", "Cát Bà", JsonValueKind.Null), (copiedSeed.GetProperty("supplierLotCode").GetString(), copiedSeed.GetProperty("actualSource").GetString(), copiedSeed.GetProperty("seedLotId").ValueKind));
        Assert.Empty(copy.GetProperty("payments").EnumerateArray());
        Assert.NotEqual(source.LineOf(world.Seed), copy.LineOf(world.Seed));
        Assert.Equal(0, await factory.QueryAsync(db => db.SeedReceiptLots.CountAsync(l => l.PurchaseReceiptLine.ReceiptId == copy.Id())));

        var copied = await factory.QueryAsync(db => db.AuditEvents.SingleAsync(e => e.RecordId == copy.Id() && e.Action == "PurchaseReceipt.Copied"));
        Assert.Contains(source.Id().ToString(), copied.Before);

        // Cancelling the copy leaves the source as it was.
        await (await world.Owner.CancelAsync(copy.Id(), "Not needed", null)).ReadAsync(HttpStatusCode.OK);
        var sourceNow = await world.Owner.GetFromJsonAsync<JsonElement>($"/api/purchase-receipts/{source.Id()}");
        Assert.Equal(("Cancelled", "Wrong price"), (sourceNow.GetProperty("status").GetString(), sourceNow.GetProperty("cancelReason").GetString()));
        Assert.Equal(2, sourceNow.GetProperty("lines").GetArrayLength());

        // A Completed receipt can be copied too.
        var completed = await world.CompletedAsync(0, 0, new LineBody(world.Other, 2, 5000));
        var second = await (await world.Owner.PostAsync($"/api/purchase-receipts/{completed.Id()}/copy", null)).ReadAsync(HttpStatusCode.Created);
        Assert.Equal("Draft", second.GetProperty("status").GetString());
        Assert.Equal("Completed", (await world.Owner.GetFromJsonAsync<JsonElement>($"/api/purchase-receipts/{completed.Id()}")).GetProperty("status").GetString());
    }
}
