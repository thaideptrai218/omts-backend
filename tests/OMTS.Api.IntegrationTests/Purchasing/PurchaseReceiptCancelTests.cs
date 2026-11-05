using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using OMTS.Api.IntegrationTests.Infrastructure;
using OMTS.Api.IntegrationTests.Notifications;
using OMTS.Application.Features.Notifications;
using OMTS.Domain.Constants;

namespace OMTS.Api.IntegrationTests.Purchasing;

/// <summary>
/// PURCHASE-06 (SRS 27/10): a Draft is removed with its lines (06.1); a Completed receipt is cancelled with a reason by the
/// person who recorded it or the Owner (BR-PURCHASE-09), never once a seed lot was seeded or with a purchase return
/// (MSG-PURCHASE-09); payments are never cancelled with it, so the debt may go below zero (MSG-PURCHASE-07 before,
/// MSG-PURCHASE-08 to the Accountant and the Owner after).
/// </summary>
public class PurchaseReceiptCancelTests(AuthApiFactory factory) : IClassFixture<AuthApiFactory>
{
    [Fact]
    public async Task Removing_a_draft_deletes_it_with_its_lines_and_only_the_audit_log_keeps_it()
    {
        var world = await factory.WorldAsync();
        var author = await factory.ZoneManagerAsync(world.Zone);
        var colleague = await factory.ZoneManagerAsync(world.Zone);
        var draft = await author.CreateReceiptAsync(world.Body(new LineBody(world.Seed, 10, 900), new LineBody(world.Other, 1, 1000)));
        var url = $"/api/purchase-receipts/{draft.Id()}";

        await (await colleague.DeleteAsync(url)).ProblemAsync(HttpStatusCode.Forbidden, "MSG-GEN-08");
        await (await author.CancelAsync(draft.Id(), "Not bought")).ProblemAsync(HttpStatusCode.Conflict, "MSG-GEN-09");
        Assert.Equal(HttpStatusCode.NoContent, (await author.DeleteAsync(url)).StatusCode);

        await (await author.GetAsync(url)).ReadAsync(HttpStatusCode.NotFound);
        Assert.Equal(0, await factory.QueryAsync(db => db.PurchaseReceipts.CountAsync(r => r.Id == draft.Id())));
        Assert.Equal(0, await factory.QueryAsync(db => db.PurchaseReceiptLines.CountAsync(l => l.ReceiptId == draft.Id())));
        var removed = await factory.QueryAsync(db => db.AuditEvents.SingleAsync(e => e.RecordId == draft.Id() && e.Action == "PurchaseReceipt.Removed"));
        Assert.Equal(draft.GetProperty("code").GetString(), removed.RecordCode);
        Assert.Contains(world.Seed.ToString(), removed.Before);
        await (await author.DeleteAsync(url)).ReadAsync(HttpStatusCode.NotFound);

        // The Owner removes any draft; a Completed receipt is cancelled, never removed.
        var other = await author.CreateReceiptAsync(world.Body(new LineBody(world.Other, 1, 1000)));
        Assert.Equal(HttpStatusCode.NoContent, (await world.Owner.DeleteAsync($"/api/purchase-receipts/{other.Id()}")).StatusCode);
        var completed = await world.CompletedAsync(new LineBody(world.Other, 1, 1000));
        await (await world.Owner.DeleteAsync($"/api/purchase-receipts/{completed.Id()}")).ProblemAsync(HttpStatusCode.Conflict, "MSG-GEN-09");
    }

    [Fact]
    public async Task A_zone_manager_cancels_own_completed_receipts_with_a_reason_and_the_owner_cancels_any()
    {
        var world = await factory.WorldAsync();
        var author = await factory.ZoneManagerAsync(world.Zone);
        var colleague = await factory.ZoneManagerAsync(world.Zone);
        var receipt = await author.CreateReceiptAsync(world.Body(world.MaterialForRaft(2, 50_000), new LineBody(world.Seed, 10, 900)) with { Complete = true });

        await (await colleague.CancelAsync(receipt.Id(), "Wrong supplier")).ProblemAsync(HttpStatusCode.Forbidden, "MSG-GEN-08");
        await (await colleague.GetAsync($"/api/purchase-receipts/{receipt.Id()}/cancel-preview")).ProblemAsync(HttpStatusCode.Forbidden, "MSG-GEN-08");
        Assert.Equal("Reason", await (await author.CancelAsync(receipt.Id(), "  ")).FieldAsync(HttpStatusCode.BadRequest, "MSG-GEN-01"));
        Assert.Equal(100_000m, await MaterialCostAsync(world));

        var cancelled = await (await author.CancelAsync(receipt.Id(), "Wrong supplier")).ReadAsync(HttpStatusCode.OK);

        Assert.Equal(("Cancelled", "Wrong supplier"), (cancelled.GetProperty("status").GetString(), cancelled.GetProperty("cancelReason").GetString()));
        Assert.Null(cancelled.SeedLotCodeOf(world.Seed));
        Assert.Equal(0m, await MaterialCostAsync(world));
        Assert.Equal(0m, (await world.Owner.GetFromJsonAsync<JsonElement>($"/api/suppliers/{world.SupplierId}/debt")).GetProperty("totals").Money("outstanding"));
        var audit = await factory.QueryAsync(db => db.AuditEvents.SingleAsync(e => e.RecordId == receipt.Id() && e.Action == "PurchaseReceipt.Cancelled"));
        Assert.Equal("Wrong supplier", audit.Reason);
        await (await author.CancelAsync(receipt.Id(), "Again")).ProblemAsync(HttpStatusCode.Conflict, "MSG-GEN-09");

        var managersReceipt = await author.CreateReceiptAsync(world.Body(new LineBody(world.Other, 1, 1000)) with { Complete = true });
        await (await world.Owner.CancelAsync(managersReceipt.Id(), "Entered twice")).ReadAsync(HttpStatusCode.OK);
    }

    [Fact]
    public async Task A_seeded_seed_lot_or_a_purchase_return_blocks_the_cancellation_and_MSG_PURCHASE_09_names_the_cause()
    {
        var world = await factory.WorldAsync();
        var seeded = await world.CompletedAsync(new LineBody(world.Other, 1, 1000), new LineBody(world.Seed, 100, 9_000));
        await factory.SeedAsync(world.Raft.Id, seeded.LineOf(world.Seed), 5);
        var lot = seeded.SeedLotCodeOf(world.Seed);

        // The block is checked first (step 1): a missing reason does not hide it.
        var problem = await (await world.Owner.CancelAsync(seeded.Id(), null)).ProblemAsync(HttpStatusCode.Conflict, "MSG-PURCHASE-09");
        Assert.Equal($"Receipt {seeded.GetProperty("code").GetString()} cannot be cancelled: seed lot {lot} has already been seeded. The Cooperative Owner can correct it instead.",
            problem.GetProperty("detail").GetString());
        Assert.Equal(("Seeded", lot), (problem.GetProperty("cause").GetString(), problem.GetProperty("causeCode").GetString()));
        var preview = await (await world.Owner.GetAsync($"/api/purchase-receipts/{seeded.Id()}/cancel-preview")).ReadAsync(HttpStatusCode.OK);
        Assert.False(preview.GetProperty("canCancel").GetBoolean());
        Assert.Equal(("Seeded", lot, "MSG-PURCHASE-09"), (preview.GetProperty("blocked").GetProperty("kind").GetString(),
            preview.GetProperty("blocked").GetProperty("code").GetString(), preview.GetProperty("blocked").GetProperty("messageCode").GetString()));

        var returned = await world.CompletedAsync(new LineBody(world.Other, 4, 25_000));
        var withReturn = await (await world.Owner.PostReturnAsync(returned.Id(), new ReturnBody
        {
            DebtReductionAmount = 25_000, Lines = [new ReturnLineBody(returned.LineOf(world.Other), 1)],
        })).ReadAsync(HttpStatusCode.Created);
        var returnCode = withReturn.GetProperty("returns")[0].GetProperty("code").GetString();
        var returnProblem = await (await world.Owner.CancelAsync(returned.Id(), "Wrong")).ProblemAsync(HttpStatusCode.Conflict, "MSG-PURCHASE-09");
        Assert.Equal(("Returned", returnCode), (returnProblem.GetProperty("cause").GetString(), returnProblem.GetProperty("causeCode").GetString()));
        Assert.Equal(StatusIds.PurchaseReceipt.Completed, await factory.QueryAsync(db => db.PurchaseReceipts.Where(r => r.Id == returned.Id()).Select(r => r.StatusId).SingleAsync()));
    }

    [Fact]
    public async Task Payments_are_never_cancelled_with_a_receipt_and_a_debt_below_zero_is_warned_then_notified_to_the_accountant_and_owner()
    {
        var world = await factory.WorldAsync();
        var manager = await factory.ZoneManagerAsync(world.Zone);
        var accountant = await factory.AccountantAsync();
        var paid = await manager.CreateReceiptAsync(world.Body(new LineBody(world.Other, 5, 100_000)) with { Complete = true });
        await (await world.Owner.PostPaymentAsync(new PaymentBody { SupplierId = world.SupplierId, ReceiptId = paid.Id(), Amount = 300_000 })).ReadAsync(HttpStatusCode.Created);
        var unpaid = await manager.CreateReceiptAsync(world.Body(new LineBody(world.Other, 1, 100_000)) with { Complete = true });

        // Debt 200,000 + 100,000. Cancelling the unpaid receipt keeps it above zero: no warning, no notification.
        var keepsDebt = await (await manager.GetAsync($"/api/purchase-receipts/{unpaid.Id()}/cancel-preview")).ReadAsync(HttpStatusCode.OK);
        Assert.Equal((true, 300_000m, 200_000m, 0m), (keepsDebt.GetProperty("canCancel").GetBoolean(), keepsDebt.Money("debtBefore"), keepsDebt.Money("debtAfter"), keepsDebt.Money("advanceAfter")));
        Assert.Equal(JsonValueKind.Null, keepsDebt.GetProperty("warning").ValueKind);
        await (await manager.CancelAsync(unpaid.Id(), "Entered twice")).ReadAsync(HttpStatusCode.OK);

        var advance = await (await manager.GetAsync($"/api/purchase-receipts/{paid.Id()}/cancel-preview")).ReadAsync(HttpStatusCode.OK);
        var supplierName = await factory.QueryAsync(db => db.Suppliers.Where(s => s.Id == world.SupplierId).Select(s => s.Name).SingleAsync());
        Assert.Equal((200_000m, -300_000m, 300_000m, "MSG-PURCHASE-07"),
            (advance.Money("debtBefore"), advance.Money("debtAfter"), advance.Money("advanceAfter"), advance.GetProperty("warningCode").GetString()));
        Assert.Equal($"After this cancellation, the cooperative will have paid {supplierName} 300,000 VND in advance. The payments already recorded are not cancelled.",
            advance.GetProperty("warning").GetString());

        await (await manager.CancelAsync(paid.Id(), "Wrong supplier")).ReadAsync(HttpStatusCode.OK);

        var payment = await factory.QueryAsync(db => db.SupplierPayments.SingleAsync(p => p.SupplierId == world.SupplierId));
        Assert.Equal((StatusIds.SupplierPayment.Recorded, (Guid?)null), (payment.StatusId, payment.ReceiptId));
        Assert.Equal(-300_000m, (await world.Owner.GetFromJsonAsync<JsonElement>($"/api/suppliers/{world.SupplierId}/debt")).GetProperty("totals").Money("outstanding"));

        var code = paid.GetProperty("code").GetString();
        foreach (var recipient in new[] { accountant, world.Owner })
        {
            var notification = Assert.Single((await recipient.NotificationPageAsync()).Items(),
                n => n.GetProperty("type").GetString() == NotificationTypes.PurchaseReceiptCancelledPaidInAdvance);
            Assert.Equal(("MSG-PURCHASE-08", "PurchaseReceipt", paid.Id(), code),
                (notification.GetProperty("messageCode").GetString(), notification.GetProperty("relatedRecordType").GetString(),
                notification.GetProperty("relatedRecordId").GetGuid(), notification.GetProperty("relatedRecordCode").GetString()));
            var parameters = notification.GetProperty("messageParams");
            Assert.Equal((code, supplierName, 300_000m), (parameters.GetProperty("receipt_code").GetString(), parameters.GetProperty("supplier_name").GetString(), parameters.GetProperty("advance_amount").GetDecimal()));
            Assert.False(notification.GetProperty("mandatory").GetBoolean());
            // The Accountant reads purchase receipts (PURCHASE-03 R), so the notification opens.
            await (await recipient.OpenNotificationAsync(notification.Id())).ReadAsync(HttpStatusCode.OK);
        }

        // The Zone Manager has no supplier debt function, so no notification; the cancellation that kept the debt sent none.
        Assert.DoesNotContain((await manager.NotificationPageAsync()).Items(), n => n.GetProperty("type").GetString() == NotificationTypes.PurchaseReceiptCancelledPaidInAdvance);
        Assert.False(await factory.QueryAsync(db => db.Notifications.AnyAsync(n => n.RelatedRecordId == unpaid.Id() && n.Type == NotificationTypes.PurchaseReceiptCancelledPaidInAdvance)));
    }

    private async Task<decimal> MaterialCostAsync(PurchasingWorld world) =>
        (await world.Owner.GetFromJsonAsync<JsonElement>($"/api/rafts/{world.Raft.Id}")).Money("materialCost");
}
