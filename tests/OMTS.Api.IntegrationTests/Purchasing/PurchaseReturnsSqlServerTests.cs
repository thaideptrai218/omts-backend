using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using ClosedXML.Excel;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using OMTS.Api.IntegrationTests.Infrastructure;
using OMTS.Domain.Constants;
using static OMTS.Api.IntegrationTests.Infrastructure.AcsTestKit;
using static OMTS.Api.IntegrationTests.Purchasing.PurchasingSqlServerKit;
using static OMTS.Api.IntegrationTests.Purchasing.PurchasingTestKit;
using FarmingHost = OMTS.Api.IntegrationTests.Farming.FarmingSqlServerHost;

namespace OMTS.Api.IntegrationTests.Purchasing;

/// <summary>
/// PURCHASE-02 behaviour only a real SQL Server shows: rowversion conflicts on the receipt row under parallel returns and
/// cancels, the status catalog and its foreign key, and the debt, statement and export summed by SQL with Cancelled returns.
/// </summary>
public class PurchaseReturnsSqlServerTests
{
    private static async Task AssertLosersAsync(IEnumerable<HttpResponseMessage> losers, params string[] allowed)
    {
        foreach (var loser in losers)
        {
            Assert.Contains(await loser.CodeOrStatusAsync(), allowed);
        }
    }

    private static Task<decimal> RecordedAsync(FarmingHost host, Guid line) => host.QueryAsync(db => db.PurchaseReturnLines
        .Where(l => l.ReceiptLineId == line && l.Return.StatusId == StatusIds.PurchaseReturn.Recorded).SumAsync(l => l.Quantity));

    // The quantity freed by a cancellation can be returned again, and only once, whoever asks at the same moment.
    [SqlServerFact]
    public async Task Parallel_returns_of_a_cancelled_returns_quantity_never_exceed_what_it_freed()
    {
        await using var host = await FarmingHost.StartAsync();
        var world = await host.WorldAsync();
        var manager = await host.ZoneManagerAsync(world.Zone.Id);
        var receipt = await world.CompletedAsync(new LineBody(world.Seed, 100, 10_000));
        var line = receipt.LineOf(world.Seed);
        var all = await world.ReturnAsync(receipt.Id(), line, 100);
        await (await world.Owner.CancelReturnAsync(all.GetProperty("returns")[0].Id(), "Not sent back")).ReadAsync(HttpStatusCode.OK);

        var responses = await InParallelAsync(6, i => (i % 2 == 0 ? world.Owner : manager).PostReturnAsync(
            receipt.Id(), new ReturnBody { OwnerApproved = true, Lines = [new ReturnLineBody(line, 40)] }));

        var created = responses.Count(r => r.StatusCode == HttpStatusCode.Created);
        Assert.InRange(created, 1, 2);
        await AssertLosersAsync(responses.Where(r => r.StatusCode != HttpStatusCode.Created), "409 MSG06", "400 MSG-PURCHASE-03");
        Assert.Equal(created * 40m, await RecordedAsync(host, line));
        var view = await world.Owner.GetFromJsonAsync<JsonElement>($"/api/purchase-receipts/{receipt.Id()}");
        Assert.Equal((created * 400_000m, 100m - created * 40m), (view.Money("returned"), view.LineViewOf(world.Seed).Money("returnable")));
    }

    [SqlServerFact]
    public async Task Parallel_cancels_of_one_return_cancel_it_once_with_one_audit_row()
    {
        await using var host = await FarmingHost.StartAsync();
        var world = await host.WorldAsync();
        var receipt = await world.CompletedAsync(new LineBody(world.Other, 4, 25_000));
        var returnId = (await world.ReturnAsync(receipt.Id(), receipt.LineOf(world.Other), 1)).GetProperty("returns")[0].Id();

        var responses = await InParallelAsync(5, _ => world.Owner.CancelReturnAsync(returnId, "Not sent back"));

        Assert.Single(responses, r => r.StatusCode == HttpStatusCode.OK);
        await AssertLosersAsync(responses.Where(r => r.StatusCode != HttpStatusCode.OK), "409 MSG06", "409 MSG-GEN-09");
        Assert.Equal(1, await host.QueryAsync(db => db.AuditEvents.CountAsync(e => e.RecordId == returnId && e.Action == "PurchaseReturn.Cancelled")));
    }

    // A cancel, a return and the receipt's cancel at once: whichever wins, no Recorded return on a Cancelled receipt and the quantities hold.
    [SqlServerFact]
    public async Task Parallel_return_cancel_new_return_and_receipt_cancel_leave_a_coherent_receipt()
    {
        await using var host = await FarmingHost.StartAsync();
        var world = await host.WorldAsync();
        for (var round = 0; round < 4; round++)
        {
            var receipt = await world.CompletedAsync(new LineBody(world.Seed, 10, 10_000));
            var line = receipt.LineOf(world.Seed);
            var returnId = (await world.ReturnAsync(receipt.Id(), line, 10)).GetProperty("returns")[0].Id();

            var responses = await InParallelAsync(3, i => i switch
            {
                0 => world.Owner.CancelReturnAsync(returnId, "Not sent back"),
                1 => world.Owner.PostReturnAsync(receipt.Id(), new ReturnBody { Lines = [new ReturnLineBody(line, 5)] }),
                _ => world.Owner.CancelAsync(receipt.Id(), "Wrong"),
            });

            Assert.All(responses, r => Assert.NotEqual(HttpStatusCode.InternalServerError, r.StatusCode));
            var statusId = await host.QueryAsync(db => db.PurchaseReceipts.Where(r => r.Id == receipt.Id()).Select(r => r.StatusId).SingleAsync());
            var recorded = await RecordedAsync(host, line);
            Assert.True(recorded <= 10, $"round {round}: {recorded} returned of 10");
            Assert.False(statusId == StatusIds.PurchaseReceipt.Cancelled && recorded > 0, $"round {round}: a return still counts on a cancelled receipt");
        }
    }

    [SqlServerFact]
    public async Task A_stale_row_version_on_the_cancel_answers_MSG06_and_the_current_one_cancels_and_both_rows_move()
    {
        await using var host = await FarmingHost.StartAsync();
        var world = await host.WorldAsync();
        var receipt = await world.CompletedAsync(new LineBody(world.Other, 4, 25_000));
        var returned = await world.ReturnAsync(receipt.Id(), receipt.LineOf(world.Other), 1);
        var listed = returned.GetProperty("returns")[0];

        await (await world.Owner.CancelReturnAsync(listed.Id(), "Late", Convert.ToBase64String([0, 0, 0, 0, 0, 0, 0, 1]))).ProblemAsync(HttpStatusCode.Conflict, "MSG06");
        Assert.Equal("RowVersion", await (await world.Owner.CancelReturnAsync(listed.Id(), "Bad", "not base64!")).FieldAsync(HttpStatusCode.BadRequest, "MSG-GEN-01"));

        var cancelled = await (await world.Owner.CancelReturnAsync(listed.Id(), "Not sent back", listed.GetProperty("rowVersion").GetString())).ReadAsync(HttpStatusCode.OK);

        Assert.NotEqual(listed.GetProperty("rowVersion").GetString(), cancelled.GetProperty("returns")[0].GetProperty("rowVersion").GetString());
        // The receipt row is part of the save: a form opened before it is told so.
        Assert.NotEqual(returned.GetProperty("rowVersion").GetString(), cancelled.GetProperty("rowVersion").GetString());
    }

    // The answers of a return report no debt and it notifies nobody, so unlike a payment it takes no part in the supplier row (PM 03/11).
    [SqlServerFact]
    public async Task A_return_and_its_cancel_leave_the_supplier_row_alone_so_a_supplier_form_stays_valid()
    {
        await using var host = await FarmingHost.StartAsync();
        var world = await host.WorldAsync();
        var receipt = await world.CompletedAsync(new LineBody(world.Other, 4, 25_000));
        var before = (await world.Owner.GetFromJsonAsync<JsonElement>($"/api/suppliers/{world.SupplierId}")).GetProperty("rowVersion").GetString();

        var returned = await world.ReturnAsync(receipt.Id(), receipt.LineOf(world.Other), 1);
        await (await world.Owner.CancelReturnAsync(returned.GetProperty("returns")[0].Id(), "Not sent back")).ReadAsync(HttpStatusCode.OK);

        Assert.Equal(before, (await world.Owner.GetFromJsonAsync<JsonElement>($"/api/suppliers/{world.SupplierId}")).GetProperty("rowVersion").GetString());
    }

    [SqlServerFact]
    public async Task The_status_catalog_has_the_new_group_and_the_status_column_refuses_any_other_value()
    {
        await using var host = await FarmingHost.StartAsync();
        var world = await host.WorldAsync();
        var receipt = await world.CompletedAsync(new LineBody(world.Other, 4, 25_000));
        var returnId = (await world.ReturnAsync(receipt.Id(), receipt.LineOf(world.Other), 1)).GetProperty("returns")[0].Id();

        var group = await host.QueryAsync(db => db.Statuses.Where(s => s.Group == StatusGroups.PurchaseReturn).OrderBy(s => s.Id).Select(s => new { s.Id, s.Code, s.IsInitial, s.IsFinal }).ToListAsync());
        Assert.Equal(
            [(StatusIds.PurchaseReturn.Recorded, "RECORDED", true, false), (StatusIds.PurchaseReturn.Cancelled, "CANCELLED", false, true)],
            group.Select(s => (s.Id, s.Code, s.IsInitial, s.IsFinal)).ToList());
        var transition = await host.QueryAsync(db => db.StatusTransitions.SingleAsync(t => t.FromStatusId == StatusIds.PurchaseReturn.Recorded));
        Assert.Equal((StatusIds.PurchaseReturn.Cancelled, "PURCHASE-02", "Cooperative Owner", true), (transition.ToStatusId, transition.UseCases, transition.AllowedPerformers, transition.ReasonRequired));

        await using var db = host.Database.CreateContext();
        var failure = await Assert.ThrowsAsync<SqlException>(() => db.Database.ExecuteSqlAsync($"UPDATE PurchaseReturns SET StatusId = 999999 WHERE Id = {returnId}"));
        Assert.Equal(547, failure.Number);
    }

    // Debt, statement and export read one number: a return kept at its amount after a price correction, a cancelled return, a return above its corrected receipt.
    [SqlServerFact]
    public async Task Debt_statement_export_list_and_totals_agree_with_a_kept_amount_and_a_cancelled_return()
    {
        await using var host = await FarmingHost.StartAsync();
        var world = await host.WorldAsync();
        var owner = world.Owner;
        var tag = NewCode();
        world = world with { SupplierId = await owner.CreateSupplierAsync(name: $"{tag} NCC") };

        await owner.CreateReceiptAsync(world.Body(new LineBody(world.Other, 1.5m, 333)) with { ReceiptDate = Today.AddDays(-20), Complete = true });
        var corrected = await owner.CreateReceiptAsync(world.Body(world.MaterialForRaft(10, 10_000)) with { ReceiptDate = Today.AddDays(-10), Complete = true });
        var line = corrected.LineOf(world.Material);
        var kept = await world.ReturnAsync(corrected.Id(), line, 10);
        await (await owner.CorrectAsync(corrected.Id(), "Invoice price", new { lineId = line, unitPrice = 9_950 })).ReadAsync(HttpStatusCode.OK);
        var mistaken = await world.CompletedAsync(new LineBody(world.Other, 2, 7_000));
        var mistakenReturn = (await world.ReturnAsync(mistaken.Id(), mistaken.LineOf(world.Other), 1)).GetProperty("returns")[0].Id();
        await (await owner.CancelReturnAsync(mistakenReturn, "Not sent back")).ReadAsync(HttpStatusCode.OK);
        await world.PayAsync(1, Today.AddDays(-5));

        // 500 + 99,500 − 100,000 (the return keeps its amount, 500 above its receipt) + 14,000 − 1.
        const decimal debt = 13_999m;
        var list = await (await owner.GetAsync($"/api/supplier-debts?search={tag}")).ReadAsync(HttpStatusCode.OK);
        var row = Assert.Single(list.GetProperty("page").GetProperty("items").EnumerateArray());
        Assert.Equal((debt, 0), (row.Money("debt"), row.GetProperty("debtAgeDays").GetInt32()));

        // FIFO: the 1 paid and the 500 above the corrected receipt cover the older receipt and 1 of the last one.
        var detail = await (await owner.GetAsync($"/api/supplier-debts/{world.SupplierId}")).ReadAsync(HttpStatusCode.OK);
        Assert.Equal(debt, detail.Money("debtToday"));
        var unpaid = Assert.Single(detail.GetProperty("unpaidReceipts").EnumerateArray());
        Assert.Equal((mistaken.Id(), 1m, debt), (unpaid.Id(), unpaid.Money("covered"), unpaid.Money("remaining")));

        var from = Today.AddDays(-30);
        var statement = await (await owner.GetAsync($"/api/supplier-debts/{world.SupplierId}/statement?from={from:yyyy-MM-dd}&to={Today:yyyy-MM-dd}")).ReadAsync(HttpStatusCode.OK);
        var entries = statement.GetProperty("entries").EnumerateArray().ToList();
        Assert.Equal((0m, debt), (statement.Money("openingDebt"), statement.Money("closingDebt")));
        Assert.DoesNotContain(entries, e => e.Id() == mistakenReturn);
        Assert.Equal(
            [("Receipt", 500m), ("Receipt", 99_500m), ("Payment", -1m), ("Receipt", 14_000m), ("Return", -100_000m)],
            entries.Select(e => (e.GetProperty("kind").GetString(), e.Money("amount"))).ToList());
        Assert.Equal(kept.GetProperty("returns")[0].Id(), entries[^1].Id());

        var export = await owner.GetAsync($"/api/supplier-debts/export?supplierId={world.SupplierId}&from={from:yyyy-MM-dd}&to={Today:yyyy-MM-dd}&search={tag}");
        Assert.Equal(HttpStatusCode.OK, export.StatusCode);
        using var workbook = new XLWorkbook(await export.Content.ReadAsStreamAsync());
        Assert.Equal(debt, workbook.Worksheet("Supplier debts").RowsUsed().Skip(1).First().Cell(4).GetValue<decimal>());
        var sheet = workbook.Worksheet("Statement").RowsUsed().Skip(1).ToList();
        Assert.Equal(entries.Count + 2, sheet.Count);
        Assert.Equal(debt, sheet[^1].Cell(9).GetValue<decimal>());
        Assert.Equal(1, sheet.Count(r => r.Cell(2).GetString() == "Purchase return"));

        // The list totals read the same return: the corrected line less what it kept.
        var totals = (await owner.GetFromJsonAsync<JsonElement>($"/api/purchase-receipts?supplierId={world.SupplierId}&from={from:yyyy-MM-dd}&to={Today:yyyy-MM-dd}")).GetProperty("totals");
        Assert.Equal((114_000m, 100_000m, 14_000m), (totals.Money("receiptsTotal"), totals.Money("returnsTotal"), totals.Money("netTotal")));
    }
}
