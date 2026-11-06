using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using OMTS.Api.IntegrationTests.Infrastructure;
using static OMTS.Api.IntegrationTests.Purchasing.PurchasingTestKit;

namespace OMTS.Api.IntegrationTests.Purchasing;

/// <summary>PURCHASE-02.0 step 6, POST-1: a return is saved from a Completed receipt, dated the day it is recorded, with its code, views and audit.</summary>
public class PurchaseReturnSavedViewTests(AuthApiFactory factory) : IClassFixture<AuthApiFactory>
{
    // BR-PURCHASE-07: the return date is the day it is recorded. The money fields of the old request are simply gone.
    [Fact]
    public async Task The_return_is_dated_today_whatever_the_request_says_and_the_old_money_fields_are_ignored()
    {
        var world = await factory.WorldAsync();
        var receipt = await world.Owner.CreateReceiptAsync(world.Body(new LineBody(world.Other, 4, 25_000)) with { ReceiptDate = Today.AddDays(-5), Complete = true });

        var saved = await (await world.Owner.PostAsJsonAsync($"/api/purchase-receipts/{receipt.Id()}/returns", new
        {
            reason = "Torn", lines = new[] { new { receiptLineId = receipt.LineOf(world.Other), quantity = 1 } },
            returnDate = Today.AddDays(-3), refundAmount = 7_000, refundMethod = "Cash", debtReductionAmount = 18_000,
        })).ReadAsync(HttpStatusCode.Created);

        var listed = Assert.Single(saved.GetProperty("returns").EnumerateArray());
        Assert.Equal((Today.ToString("yyyy-MM-dd"), 25_000m), (listed.GetProperty("returnDate").GetString(), listed.Money("returnValue")));
        Assert.False(listed.TryGetProperty("refundAmount", out _));
        Assert.Equal(75_000, await world.DebtAsync());
    }

    [Fact]
    public async Task Only_a_completed_receipt_can_be_returned()
    {
        var world = await factory.WorldAsync();
        var draft = await world.Owner.CreateReceiptAsync(world.Body(new LineBody(world.Other, 1, 10_000)));
        var cancelled = await world.CompletedAsync(new LineBody(world.Other, 1, 10_000));
        await (await world.Owner.CancelAsync(cancelled.Id(), "Wrong")).ReadAsync(HttpStatusCode.OK);
        var body = new ReturnBody { Lines = [new ReturnLineBody(Guid.NewGuid(), 1)] };

        foreach (var receipt in new[] { draft, cancelled })
        {
            var problem = await (await world.Owner.PostReturnAsync(receipt.Id(), body with { Lines = [new ReturnLineBody(receipt.LineOf(world.Other), 1)] }))
                .ProblemAsync(HttpStatusCode.Conflict, "MSG-GEN-09");
            Assert.EndsWith("and cannot be used for this action.", problem.GetProperty("detail").GetString());
        }

        await (await world.Owner.PostReturnAsync(Guid.NewGuid(), body)).ReadAsync(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Returns_are_listed_with_the_receipt_and_read_one_by_one_with_audit_and_a_TH_code()
    {
        var world = await factory.WorldAsync();
        var receipt = await world.CompletedAsync(new LineBody(world.Seed, 100, 9_000));
        var line = receipt.LineOf(world.Seed);
        var created = await world.ReturnAsync(receipt.Id(), line, 10);
        var listed = Assert.Single(created.GetProperty("returns").EnumerateArray());
        Assert.Matches("^TH\\d{6}$", listed.GetProperty("code").GetString());
        Assert.Equal(("Recorded", 1901, JsonValueKind.Null), (listed.GetProperty("status").GetString(), listed.GetProperty("statusId").GetInt32(), listed.GetProperty("cancelReason").ValueKind));
        Assert.False(string.IsNullOrEmpty(listed.GetProperty("recordedBy").GetProperty("name").GetString()));

        var list = await (await world.Owner.GetAsync($"/api/purchase-receipts/{receipt.Id()}/returns")).ReadAsync(HttpStatusCode.OK);
        Assert.Equal(listed.Id(), Assert.Single(list.EnumerateArray()).Id());

        var detail = await (await world.Owner.GetAsync($"/api/purchase-returns/{listed.Id()}")).ReadAsync(HttpStatusCode.OK);
        Assert.Equal((receipt.Id(), receipt.GetProperty("code").GetString(), 90_000m, "Recorded"),
            (detail.GetProperty("receiptId").GetGuid(), detail.GetProperty("receiptCode").GetString(), detail.Money("returnValue"), detail.GetProperty("status").GetString()));
        Assert.False(detail.TryGetProperty("refundAmount", out _));
        var detailLine = Assert.Single(detail.GetProperty("lines").EnumerateArray());
        Assert.Equal((line, 10m, 90_000m, "kg"), (detailLine.GetProperty("receiptLineId").GetGuid(), detailLine.Money("quantity"), detailLine.Money("returnValue"), detailLine.GetProperty("unit").GetString()));
        await (await world.Owner.GetAsync($"/api/purchase-returns/{Guid.NewGuid()}")).ReadAsync(HttpStatusCode.NotFound);

        var audit = await factory.QueryAsync(db => db.AuditEvents.SingleAsync(e => e.RecordId == listed.Id()));
        Assert.Equal(("PurchaseReturn.Created", (Guid?)null, "Weak seed", listed.GetProperty("code").GetString()), (audit.Action, audit.ZoneId, audit.Reason, audit.RecordCode));
        Assert.Contains("90000", audit.After);
        Assert.Contains(line.ToString(), audit.After);
    }
}
