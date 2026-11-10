using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using OMTS.Api.IntegrationTests.Infrastructure;
using OMTS.Domain.Constants;
using static OMTS.Api.IntegrationTests.Purchasing.PurchasingTestKit;

namespace OMTS.Api.IntegrationTests.Purchasing;

/// <summary>
/// PURCHASE-02.1, BR-PURCHASE-07: the Owner cancels a return recorded by mistake; it frees its quantity, unblocks the receipt's
/// cancel and counts nowhere. PM 03/11: a return keeps the amount it had when recorded, whatever the unit price becomes.
/// </summary>
public class PurchaseReturnCancelTests(AuthApiFactory factory) : IClassFixture<AuthApiFactory>
{
    private static Guid FirstReturn(JsonElement receipt) => receipt.GetProperty("returns")[0].Id();

    private async Task<JsonElement> PreviewAsync(PurchasingWorld world, Guid receiptId) =>
        await (await world.Owner.GetAsync($"/api/purchase-receipts/{receiptId}/cancel-preview")).ReadAsync(HttpStatusCode.OK);

    [Fact]
    public async Task A_cancelled_return_frees_its_quantity_and_unblocks_the_cancel_of_the_receipt()
    {
        var world = await factory.WorldAsync();
        var receipt = await world.CompletedAsync(new LineBody(world.Other, 4, 25_000));
        var line = receipt.LineOf(world.Other);
        var returned = await world.ReturnAsync(receipt.Id(), line, 4);
        Assert.Equal(0m, returned.LineViewOf(world.Other).Money("returnable"));
        Assert.Equal(0, await world.DebtAsync());
        await (await world.Owner.PostReturnAsync(receipt.Id(), new ReturnBody { Lines = [new ReturnLineBody(line, 1)] })).ProblemAsync(HttpStatusCode.BadRequest, "MSG-PURCHASE-03");
        await (await world.Owner.CancelAsync(receipt.Id(), "Wrong")).ProblemAsync(HttpStatusCode.Conflict, "MSG-PURCHASE-09");

        var cancelled = await (await world.Owner.CancelReturnAsync(FirstReturn(returned), " Not sent back ")).ReadAsync(HttpStatusCode.OK);

        var listed = Assert.Single(cancelled.GetProperty("returns").EnumerateArray());
        Assert.Equal(("Cancelled", StatusIds.PurchaseReturn.Cancelled, "Not sent back", 100_000m),
            (listed.GetProperty("status").GetString(), listed.GetProperty("statusId").GetInt32(), listed.GetProperty("cancelReason").GetString(), listed.Money("returnValue")));
        Assert.NotEqual(JsonValueKind.Null, listed.GetProperty("cancelledAt").ValueKind);
        Assert.Equal((0m, 0m, 4m), (cancelled.Money("returned"), cancelled.LineViewOf(world.Other).Money("returnedQuantity"), cancelled.LineViewOf(world.Other).Money("returnable")));
        Assert.Equal(100_000, await world.DebtAsync());

        var preview = await PreviewAsync(world, receipt.Id());
        Assert.Equal((true, 100_000m, 0m), (preview.GetProperty("canCancel").GetBoolean(), preview.Money("debtBefore"), preview.Money("debtAfter")));
        await (await world.Owner.CancelAsync(receipt.Id(), "Wrong")).ReadAsync(HttpStatusCode.OK);
        Assert.Equal(0, await world.DebtAsync());
        // The return stays listed on the Cancelled receipt, with its reason; it cannot be cancelled again.
        await (await world.Owner.CancelReturnAsync(FirstReturn(returned), "Again")).ProblemAsync(HttpStatusCode.Conflict, "MSG-GEN-09");
    }

    // Finding F5 of the QA of P2: what leaves the debt with a receipt is its total less only the returns still counted in the debt.
    [Fact]
    public async Task The_cancel_preview_counts_only_the_returns_that_are_not_cancelled()
    {
        var world = await factory.WorldAsync();
        var receipt = await world.PaidAsync(40_000, new LineBody(world.Other, 4, 25_000));
        var line = receipt.LineOf(world.Other);
        var mistaken = FirstReturn(await world.ReturnAsync(receipt.Id(), line, 1));
        await (await world.Owner.CancelReturnAsync(mistaken, "Not sent back")).ReadAsync(HttpStatusCode.OK);
        var standing = (await world.ReturnAsync(receipt.Id(), line, 2)).GetProperty("returns").EnumerateArray().Single(r => r.Id() != mistaken);

        // 100,000 − 50,000 − 40,000; blocked by the return still recorded, never by the cancelled one.
        var blocked = await PreviewAsync(world, receipt.Id());
        Assert.Equal(("Returned", standing.GetProperty("code").GetString()), (blocked.GetProperty("blocked").GetProperty("kind").GetString(), blocked.GetProperty("blocked").GetProperty("code").GetString()));
        Assert.Equal((10_000m, -40_000m), (blocked.Money("debtBefore"), blocked.Money("debtAfter")));

        await (await world.Owner.CancelReturnAsync(standing.Id(), "Not sent back either")).ReadAsync(HttpStatusCode.OK);
        var open = await PreviewAsync(world, receipt.Id());
        Assert.Equal((true, 60_000m, -40_000m, 40_000m, "MSG-PURCHASE-07"),
            (open.GetProperty("canCancel").GetBoolean(), open.Money("debtBefore"), open.Money("debtAfter"), open.Money("advanceAfter"), open.GetProperty("warningCode").GetString()));
        await (await world.Owner.CancelAsync(receipt.Id(), "Wrong")).ReadAsync(HttpStatusCode.OK);
        Assert.Equal(-40_000, await world.DebtAsync());
    }

    [Fact]
    public async Task Only_the_owner_cancels_with_a_reason_and_only_a_recorded_return_with_the_audit_kept()
    {
        var world = await factory.WorldAsync();
        var manager = await factory.ZoneManagerAsync(world.Zone);
        var accountant = await factory.AccountantAsync();
        var receipt = await manager.CreateReceiptAsync(world.Body(new LineBody(world.Other, 4, 25_000)) with { Complete = true });
        var returned = await (await manager.PostReturnAsync(receipt.Id(), new ReturnBody { OwnerApproved = true, Lines = [new ReturnLineBody(receipt.LineOf(world.Other), 1)] }))
            .ReadAsync(HttpStatusCode.Created);
        var returnId = FirstReturn(returned);

        // Even the Zone Manager who recorded it does not cancel it, nor does the Accountant or an anonymous caller.
        foreach (var refused in new[] { manager, accountant })
        {
            await (await refused.CancelReturnAsync(returnId, "Mine")).ProblemAsync(HttpStatusCode.Forbidden, "MSG-GEN-08");
        }

        Assert.Equal(HttpStatusCode.Unauthorized, (await factory.CreateClient().CancelReturnAsync(returnId, "Nobody")).StatusCode);
        foreach (var reason in new[] { null, "  ", new string('r', 1001) })
        {
            Assert.Equal("Reason", await (await world.Owner.CancelReturnAsync(returnId, reason)).FieldAsync(HttpStatusCode.BadRequest, "MSG-GEN-01"));
        }

        await (await world.Owner.CancelReturnAsync(Guid.NewGuid(), "Nothing")).ReadAsync(HttpStatusCode.NotFound);
        Assert.Equal(75_000, await world.DebtAsync());

        await (await world.Owner.CancelReturnAsync(returnId, new string('r', 1000))).ReadAsync(HttpStatusCode.OK);
        await (await world.Owner.CancelReturnAsync(returnId, "Again")).ProblemAsync(HttpStatusCode.Conflict, "MSG-GEN-09");

        var audit = await factory.QueryAsync(db => db.AuditEvents.SingleAsync(e => e.RecordId == returnId && e.Action == "PurchaseReturn.Cancelled"));
        Assert.Equal((new string('r', 1000), (Guid?)null), (audit.Reason, audit.ZoneId));
        Assert.Contains("RECORDED", audit.Before);
        Assert.Contains("CANCELLED", audit.After);
        var detail = await (await world.Owner.GetAsync($"/api/purchase-returns/{returnId}")).ReadAsync(HttpStatusCode.OK);
        Assert.Equal(("Cancelled", new string('r', 1000)), (detail.GetProperty("status").GetString(), detail.GetProperty("cancelReason").GetString()));
    }

    [Fact]
    public async Task A_cancelled_return_counts_in_no_total_statement_or_unpaid_receipt()
    {
        var world = await factory.WorldAsync();
        var receipt = await world.CompletedAsync(world.MaterialForRaft(10, 10_000));
        var mistaken = FirstReturn(await world.ReturnAsync(receipt.Id(), receipt.LineOf(world.Material), 3));
        await (await world.Owner.CancelReturnAsync(mistaken, "Not sent back")).ReadAsync(HttpStatusCode.OK);

        var totals = (await world.Owner.GetFromJsonAsync<JsonElement>($"/api/purchase-receipts?supplierId={world.SupplierId}&raftId={world.Raft.Id}")).GetProperty("totals");
        Assert.Equal((100_000m, 0m, 100_000m), (totals.Money("receiptsTotal"), totals.Money("returnsTotal"), totals.Money("netTotal")));
        // The raft material cost comes from the material used on the raft, never from the receipt totals.
        Assert.Equal(JsonValueKind.Null, totals.GetProperty("raftMaterialCost").ValueKind);

        var statement = await world.Owner.GetFromJsonAsync<JsonElement>($"/api/supplier-debts/{world.SupplierId}/statement");
        Assert.Equal(["Receipt"], statement.GetProperty("entries").EnumerateArray().Select(e => e.GetProperty("kind").GetString()));
        var detail = await world.Owner.GetFromJsonAsync<JsonElement>($"/api/supplier-debts/{world.SupplierId}");
        var unpaid = Assert.Single(detail.GetProperty("unpaidReceipts").EnumerateArray());
        Assert.Equal((0m, 100_000m), (unpaid.Money("returned"), unpaid.Money("remaining")));
    }

    // PM 03/11, in place of the wording of BR-PURCHASE-07: the amount is the one of the day it was recorded.
    [Fact]
    public async Task A_return_keeps_its_amount_after_a_price_correction_and_a_new_return_takes_the_corrected_price()
    {
        var world = await factory.WorldAsync();
        var receipt = await world.CompletedAsync(world.MaterialForRaft(10, 10_000));
        var line = receipt.LineOf(world.Material);
        var first = FirstReturn(await world.ReturnAsync(receipt.Id(), line, 2));

        var corrected = await (await world.Owner.CorrectAsync(receipt.Id(), "Invoice price", new { lineId = line, unitPrice = 5_000 })).ReadAsync(HttpStatusCode.OK);

        Assert.Equal((50_000m, 20_000m), (corrected.Money("total"), corrected.Money("returned")));
        Assert.Equal(20_000m, Assert.Single(corrected.GetProperty("returns").EnumerateArray()).Money("returnValue"));
        Assert.Equal(20_000m, (await world.Owner.GetFromJsonAsync<JsonElement>($"/api/purchase-returns/{first}")).Money("returnValue"));
        Assert.Equal(30_000, await world.DebtAsync());
        // A material used now is costed at the corrected price, the latest the item was bought at.
        await factory.UseMaterialAsync(world.Raft.Id, world.Material, 1);
        Assert.Equal(5_000, await world.Owner.RaftMaterialCostAsync(world.Raft.Id));
        var statement = await world.Owner.GetFromJsonAsync<JsonElement>($"/api/supplier-debts/{world.SupplierId}/statement");
        Assert.Equal([("Receipt", 50_000m), ("Return", -20_000m)], statement.GetProperty("entries").EnumerateArray().Select(e => (e.GetProperty("kind").GetString(), e.Money("amount"))).ToList());

        var second = await world.ReturnAsync(receipt.Id(), line, 1);
        Assert.Equal(25_000m, second.Money("returned"));
        Assert.Equal(25_000, await world.DebtAsync());

        // Cancelling gives back exactly what the return took, not what it would be worth now.
        await (await world.Owner.CancelReturnAsync(first, "Not sent back")).ReadAsync(HttpStatusCode.OK);
        Assert.Equal(45_000, await world.DebtAsync());
    }

    [Fact]
    public async Task The_owners_correction_counts_only_returns_not_cancelled_and_refuses_a_receipt_left_with_none()
    {
        var world = await factory.WorldAsync();
        var receipt = await world.CompletedAsync(new LineBody(world.Other, 4, 25_000));
        var line = receipt.LineOf(world.Other);
        var mistaken = FirstReturn(await world.ReturnAsync(receipt.Id(), line, 2));
        await (await world.Owner.CancelReturnAsync(mistaken, "Not sent back")).ReadAsync(HttpStatusCode.OK);
        var standing = (await world.ReturnAsync(receipt.Id(), line, 1)).GetProperty("returns").EnumerateArray().Single(r => r.Id() != mistaken).Id();

        var below = await (await world.Owner.CorrectAsync(receipt.Id(), "Counted again", new { lineId = line, quantity = 0.5m })).ProblemAsync(HttpStatusCode.BadRequest, "MSG-PURCHASE-10");
        Assert.Equal(1m, below.Money("returnedQuantity"));
        await (await world.Owner.CorrectAsync(receipt.Id(), "Counted again", new { lineId = line, quantity = 1 })).ReadAsync(HttpStatusCode.OK);

        // With no return left that counts, the receipt can be cancelled again, so it is no longer corrected (BR-PURCHASE-09).
        await (await world.Owner.CancelReturnAsync(standing, "Not sent back either")).ReadAsync(HttpStatusCode.OK);
        await (await world.Owner.CorrectAsync(receipt.Id(), "Counted again", new { lineId = line, quantity = 2 })).ProblemAsync(HttpStatusCode.Conflict, "MSG-GEN-09");
    }
}
