using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using ClosedXML.Excel;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.DependencyInjection;
using OMTS.Api.IntegrationTests.Infrastructure;
using OMTS.Application.Common.Constants;
using OMTS.Application.Common.Interfaces.Persistence;
using OMTS.Application.Features.Notifications;
using OMTS.Domain.Constants;
using OMTS.Domain.Enums;
using static OMTS.Api.IntegrationTests.Infrastructure.AcsTestKit;
using static OMTS.Api.IntegrationTests.Purchasing.PurchasingSqlServerKit;
using static OMTS.Api.IntegrationTests.Purchasing.PurchasingTestKit;
using FarmingHost = OMTS.Api.IntegrationTests.Farming.FarmingSqlServerHost;

namespace OMTS.Api.IntegrationTests.Purchasing;

/// <summary>
/// QA of PURCHASE-02 on a real, migrated SQL Server database (report qa-261104-purchase-returns.md). The races are made certain, not
/// a matter of timing: a request is held when it takes its document number, after every check and before its save, while another save runs.
/// A test named Finding_* shows a defect and fails on purpose until it is fixed.
/// </summary>
public class PurchaseReturnsQaSqlServerTests
{
    /// <summary>The first request to take a number of the key waits here for the other save to finish.</summary>
    private sealed class HeldNumbers(IDocumentNumberAllocator inner, string key, Func<Task> meanwhile, Once once) : IDocumentNumberAllocator
    {
        public async Task<long> NextAsync(string requested, CancellationToken cancellationToken)
        {
            if (requested == key && once.First())
            {
                await meanwhile();
            }

            return await inner.NextAsync(requested, cancellationToken);
        }
    }

    private sealed class Once
    {
        private int _taken;

        public bool First() => Interlocked.Increment(ref _taken) == 1;
    }

    private static WebApplicationFactory<Program> HoldingAt(FarmingHost host, string key, Func<Task> meanwhile)
    {
        var once = new Once();
        return host.Api.WithWebHostBuilder(b => b.ConfigureTestServices(services =>
        {
            var real = services.Single(d => d.ServiceType == typeof(IDocumentNumberAllocator));
            services.Remove(real);
            services.AddScoped<IDocumentNumberAllocator>(provider =>
                new HeldNumbers((IDocumentNumberAllocator)ActivatorUtilities.CreateInstance(provider, real.ImplementationType!), key, meanwhile, once));
        }));
    }

    /// <summary>The first reading of a debt waits here for the other save to finish.</summary>
    private sealed class HeldDebts(ISupplierDebtQuery inner, Func<Task> meanwhile, Once once) : ISupplierDebtQuery
    {
        public async Task<IReadOnlyDictionary<Guid, SupplierDebtSums>> SumsAsync(Guid? supplierId, DateOnly asOf, CancellationToken cancellationToken)
        {
            var sums = await inner.SumsAsync(supplierId, asOf, cancellationToken);
            if (once.First())
            {
                await meanwhile();
            }

            return sums;
        }

        public Task<IReadOnlyList<DebtSupplierRow>> SuppliersAsync(IReadOnlyCollection<Guid> supplierIds, string? search, CancellationToken cancellationToken) =>
            inner.SuppliersAsync(supplierIds, search, cancellationToken);

        public Task<IReadOnlyList<DebtReceiptRow>> ReceiptsAsync(IReadOnlyCollection<Guid> supplierIds, CancellationToken cancellationToken) =>
            inner.ReceiptsAsync(supplierIds, cancellationToken);

        public Task<IReadOnlyList<DebtMoneyBackRow>> MoneyBackAsync(IReadOnlyCollection<Guid> supplierIds, CancellationToken cancellationToken) =>
            inner.MoneyBackAsync(supplierIds, cancellationToken);

        public Task<(IReadOnlyList<DebtReceiptRow> Receipts, IReadOnlyList<StatementReturnRow> Returns, IReadOnlyList<StatementPaymentRow> Payments)> StatementRowsAsync(
            Guid supplierId, DateOnly from, DateOnly to, CancellationToken cancellationToken) =>
            inner.StatementRowsAsync(supplierId, from, to, cancellationToken);
    }

    private static WebApplicationFactory<Program> HoldingAtTheDebt(FarmingHost host, Func<Task> meanwhile)
    {
        var once = new Once();
        return host.Api.WithWebHostBuilder(b => b.ConfigureTestServices(services =>
        {
            var real = services.Single(d => d.ServiceType == typeof(ISupplierDebtQuery));
            services.Remove(real);
            services.AddScoped<ISupplierDebtQuery>(provider =>
                new HeldDebts((ISupplierDebtQuery)ActivatorUtilities.CreateInstance(provider, real.ImplementationType!), meanwhile, once));
        }));
    }

    private static HttpClient ClientOf(WebApplicationFactory<Program> api, HttpClient loggedIn)
    {
        var client = api.CreateClient();
        client.DefaultRequestHeaders.Authorization = loggedIn.DefaultRequestHeaders.Authorization;
        return client;
    }

    private static Task<decimal> RecordedAsync(FarmingHost host, Guid line) => host.QueryAsync(db => db.PurchaseReturnLines
        .Where(l => l.ReceiptLineId == line && l.Return.StatusId == StatusIds.PurchaseReturn.Recorded).SumAsync(l => l.Quantity));

    private static Guid Newest(JsonElement receipt) =>
        receipt.GetProperty("returns").EnumerateArray().OrderByDescending(r => r.GetProperty("code").GetString(), StringComparer.Ordinal).First().Id();

    private static Task<long> LastThAsync(FarmingHost host) =>
        host.QueryAsync(db => db.DocumentSequences.Where(s => s.Key == DocumentSequenceKeys.PurchaseReturn).Select(s => s.LastValue).SingleOrDefaultAsync());

    // The return checked 9 against a line of 10; the Owner lowers the line to 1 before it saves. Without the receipt row in the save, 10 would be returned of 1.
    [SqlServerFact]
    public async Task A_return_held_while_a_correction_lowers_the_line_answers_MSG06_and_the_limit_holds()
    {
        await using var host = await FarmingHost.StartAsync();
        var world = await host.WorldAsync();
        var receipt = await world.CompletedAsync(new LineBody(world.Other, 10, 1_000));
        var line = receipt.LineOf(world.Other);
        await world.ReturnAsync(receipt.Id(), line, 1);
        await using var api = HoldingAt(host, DocumentSequenceKeys.PurchaseReturn, async () =>
            await (await world.Owner.CorrectAsync(receipt.Id(), "Counted again", new { lineId = line, quantity = 1 })).ReadAsync(HttpStatusCode.OK));

        await (await ClientOf(api, world.Owner).PostReturnAsync(receipt.Id(), new ReturnBody { Lines = [new ReturnLineBody(line, 9)] })).ProblemAsync(HttpStatusCode.Conflict, "MSG06");

        Assert.Equal(1m, await RecordedAsync(host, line));
    }

    // The return would be saved at the old price; the correction changed it meanwhile.
    [SqlServerFact]
    public async Task A_return_held_while_a_correction_changes_the_price_answers_MSG06_and_the_retry_takes_the_new_price()
    {
        await using var host = await FarmingHost.StartAsync();
        var world = await host.WorldAsync();
        var receipt = await world.CompletedAsync(new LineBody(world.Other, 10, 1_000));
        var line = receipt.LineOf(world.Other);
        await world.ReturnAsync(receipt.Id(), line, 1);
        await using var api = HoldingAt(host, DocumentSequenceKeys.PurchaseReturn, async () =>
            await (await world.Owner.CorrectAsync(receipt.Id(), "Invoice price", new { lineId = line, unitPrice = 700 })).ReadAsync(HttpStatusCode.OK));
        var client = ClientOf(api, world.Owner);
        var body = new ReturnBody { Lines = [new ReturnLineBody(line, 1)] };

        await (await client.PostReturnAsync(receipt.Id(), body)).ProblemAsync(HttpStatusCode.Conflict, "MSG06");
        var saved = await (await client.PostReturnAsync(receipt.Id(), body)).ReadAsync(HttpStatusCode.Created);

        // 1,000 kept + 700 at the corrected price; 10 × 700 − 1,700.
        Assert.Equal((1_700m, 5_300m), (saved.Money("returned"), await world.DebtAsync()));
    }

    [SqlServerFact]
    public async Task A_return_held_while_another_takes_the_quantity_answers_MSG06_and_the_retry_answers_MSG_PURCHASE_03()
    {
        await using var host = await FarmingHost.StartAsync();
        var world = await host.WorldAsync();
        var receipt = await world.CompletedAsync(new LineBody(world.Other, 10, 1_000));
        var line = receipt.LineOf(world.Other);
        await using var api = HoldingAt(host, DocumentSequenceKeys.PurchaseReturn, async () => await world.ReturnAsync(receipt.Id(), line, 8));
        var client = ClientOf(api, world.Owner);
        var body = new ReturnBody { Lines = [new ReturnLineBody(line, 8)] };

        await (await client.PostReturnAsync(receipt.Id(), body)).ProblemAsync(HttpStatusCode.Conflict, "MSG06");
        var refused = await (await client.PostReturnAsync(receipt.Id(), body)).ProblemAsync(HttpStatusCode.BadRequest, "MSG-PURCHASE-03");

        Assert.Equal((2m, 8m), (refused.Money("maxQuantity"), await RecordedAsync(host, line)));
    }

    [SqlServerFact]
    public async Task A_return_held_while_the_receipt_is_cancelled_answers_MSG06_and_the_retry_MSG_GEN_09_and_no_return_counts()
    {
        await using var host = await FarmingHost.StartAsync();
        var world = await host.WorldAsync();
        var receipt = await world.CompletedAsync(new LineBody(world.Other, 10, 1_000));
        var line = receipt.LineOf(world.Other);
        await using var api = HoldingAt(host, DocumentSequenceKeys.PurchaseReturn, async () =>
            await (await world.Owner.CancelAsync(receipt.Id(), "Wrong")).ReadAsync(HttpStatusCode.OK));
        var client = ClientOf(api, world.Owner);
        var body = new ReturnBody { Lines = [new ReturnLineBody(line, 1)] };

        await (await client.PostReturnAsync(receipt.Id(), body)).ProblemAsync(HttpStatusCode.Conflict, "MSG06");
        await (await client.PostReturnAsync(receipt.Id(), body)).ProblemAsync(HttpStatusCode.Conflict, "MSG-GEN-09");

        Assert.Equal(0m, await RecordedAsync(host, line));
        Assert.Equal(0m, await world.DebtAsync());
    }

    // The other way round: the receipt's cancel passed its check of no return that counts, and a return is recorded before it saves.
    [SqlServerFact]
    public async Task A_receipt_cancel_held_while_a_return_is_recorded_answers_MSG06_and_the_retry_MSG_PURCHASE_09()
    {
        await using var host = await FarmingHost.StartAsync();
        var world = await host.WorldAsync();
        var receipt = await world.CompletedAsync(new LineBody(world.Other, 10, 1_000));
        var line = receipt.LineOf(world.Other);
        await using var api = HoldingAtTheDebt(host, async () => await world.ReturnAsync(receipt.Id(), line, 1));
        var client = ClientOf(api, world.Owner);

        await (await client.CancelAsync(receipt.Id(), "Wrong")).ProblemAsync(HttpStatusCode.Conflict, "MSG06");
        await (await client.CancelAsync(receipt.Id(), "Wrong")).ProblemAsync(HttpStatusCode.Conflict, "MSG-PURCHASE-09");

        Assert.Equal(StatusIds.PurchaseReceipt.Completed, await host.QueryAsync(db => db.PurchaseReceipts.Where(r => r.Id == receipt.Id()).Select(r => r.StatusId).SingleAsync()));
        Assert.Equal(1m, await RecordedAsync(host, line));
    }

    [SqlServerFact]
    public async Task A_return_held_while_another_return_is_cancelled_answers_MSG06_and_the_retry_may_use_the_freed_quantity()
    {
        await using var host = await FarmingHost.StartAsync();
        var world = await host.WorldAsync();
        var receipt = await world.CompletedAsync(new LineBody(world.Other, 10, 1_000));
        var line = receipt.LineOf(world.Other);
        var mistaken = Newest(await world.ReturnAsync(receipt.Id(), line, 8));
        await using var api = HoldingAt(host, DocumentSequenceKeys.PurchaseReturn, async () =>
            await (await world.Owner.CancelReturnAsync(mistaken, "Not sent back")).ReadAsync(HttpStatusCode.OK));
        var client = ClientOf(api, world.Owner);

        await (await client.PostReturnAsync(receipt.Id(), new ReturnBody { Lines = [new ReturnLineBody(line, 2)] })).ProblemAsync(HttpStatusCode.Conflict, "MSG06");
        await (await client.PostReturnAsync(receipt.Id(), new ReturnBody { Lines = [new ReturnLineBody(line, 10)] })).ReadAsync(HttpStatusCode.Created);

        Assert.Equal(10m, await RecordedAsync(host, line));
    }

    // The ground of the branch: a return that lands inside a payment is the same as a return just after it. The payment's answer and its notification were true when it was
    // read; the return's answers show no debt to contradict them. Unlike two payments, neither order is impossible, so the supplier row is not needed. MSG-PURCHASE-05
    // is not asked, as it would not be in that order either: the debt goes below zero by the return, which sends nothing (BR-PURCHASE-05).
    [SqlServerFact]
    public async Task A_return_inside_a_payment_ends_as_the_payment_followed_by_the_return_and_neither_refuses_the_other()
    {
        await using var host = await FarmingHost.StartAsync();
        var world = await host.WorldAsync();
        var receipt = await world.CompletedAsync(new LineBody(world.Other, 1, 100_000));
        await world.PayAsync(1);
        await using var api = HoldingAt(host, DocumentSequenceKeys.SupplierPayment, async () =>
            await world.ReturnAsync(receipt.Id(), receipt.LineOf(world.Other), 1));

        var payment = await (await ClientOf(api, world.Owner).PostPaymentAsync(new PaymentBody { SupplierId = world.SupplierId, Amount = 99_999 })).ReadAsync(HttpStatusCode.Created);

        Assert.Equal(0m, payment.Money("debtAfter"));
        Assert.Equal(-100_000m, await world.DebtAsync());
        var told = await host.QueryAsync(db => db.Notifications
            .Where(n => n.Type == NotificationTypes.SupplierTransaction && n.RelatedRecordId == payment.Id() && n.RecipientAccountId == host.Owner.Account.Id).Select(n => n.MessageParams).SingleAsync());
        Assert.Equal(0m, JsonDocument.Parse(told!).RootElement.GetProperty("debt_amount").GetDecimal());
    }

    // Parallel on different receipts: every code unique, no refused request, no gap; a reason of the full length fits the column.
    [SqlServerFact]
    public async Task Parallel_returns_of_different_receipts_all_save_with_unique_consecutive_TH_codes()
    {
        await using var host = await FarmingHost.StartAsync();
        var world = await host.WorldAsync();
        var receipts = new List<JsonElement>();
        for (var i = 0; i < 8; i++)
        {
            receipts.Add(await world.CompletedAsync(new LineBody(world.Other, 4, 25_000)));
        }

        var responses = await InParallelAsync(8, i => world.Owner.PostReturnAsync(
            receipts[i].Id(), new ReturnBody { Reason = new string('r', 1000), Lines = [new ReturnLineBody(receipts[i].LineOf(world.Other), 1)] }));

        Assert.All(responses, r => Assert.Equal(HttpStatusCode.Created, r.StatusCode));
        var codes = await host.QueryAsync(db => db.PurchaseReturns.Select(r => r.Code).OrderBy(c => c).ToListAsync());
        Assert.Equal(Enumerable.Range(1, 8).Select(n => $"TH{n:000000}").ToList(), codes);
    }

    // PURCHASE-02: no code before a saved return, for any refusal; a lost race leaves a gap (client list §2) and never a repeated code.
    [SqlServerFact]
    public async Task A_refused_request_spends_no_TH_number_and_a_lost_race_leaves_only_a_gap()
    {
        await using var host = await FarmingHost.StartAsync();
        var world = await host.WorldAsync();
        var manager = await host.ZoneManagerAsync(world.Zone.Id);
        var accountant = await host.StaffAsync(null, StaffRole.Accountant);
        var receipt = await world.CompletedAsync(new LineBody(world.Other, 4, 25_000));
        var line = receipt.LineOf(world.Other);
        var draft = await world.Owner.CreateReceiptAsync(world.Body(new LineBody(world.Other, 1, 1_000)));
        var one = new ReturnBody { Lines = [new ReturnLineBody(line, 1)] };

        await (await world.Owner.PostReturnAsync(receipt.Id(), one with { Reason = " " })).ProblemAsync(HttpStatusCode.BadRequest, "MSG-GEN-01");
        await (await world.Owner.PostReturnAsync(receipt.Id(), new ReturnBody { Lines = [new ReturnLineBody(line, 1.001m)] })).ProblemAsync(HttpStatusCode.BadRequest, "MSG-GEN-01");
        await (await world.Owner.PostReturnAsync(receipt.Id(), new ReturnBody { Lines = [new ReturnLineBody(line, 5)] })).ProblemAsync(HttpStatusCode.BadRequest, "MSG-PURCHASE-03");
        await (await manager.PostReturnAsync(receipt.Id(), one)).ProblemAsync(HttpStatusCode.Conflict, "MSG-PURCHASE-02");
        await (await world.Owner.PostReturnAsync(draft.Id(), new ReturnBody { Lines = [new ReturnLineBody(draft.LineOf(world.Other), 1)] })).ProblemAsync(HttpStatusCode.Conflict, "MSG-GEN-09");
        await (await accountant.PostReturnAsync(receipt.Id(), one)).ProblemAsync(HttpStatusCode.Forbidden, "MSG-GEN-08");
        Assert.Equal(0, await LastThAsync(host));

        var responses = await InParallelAsync(4, _ => world.Owner.PostReturnAsync(receipt.Id(), one));
        Assert.All(responses, r => Assert.Contains(r.StatusCode, new[] { HttpStatusCode.Created, HttpStatusCode.Conflict }));
        var codes = await host.QueryAsync(db => db.PurchaseReturns.Select(r => r.Code).ToListAsync());
        Assert.Equal(codes.Count, codes.Distinct().Count());
        Assert.Equal(4, await LastThAsync(host));
    }

    // A return in a past period: the statement of that month, the opening of the next, the list totals, the raft and the exports read one number, before and after the cancel.
    [SqlServerFact]
    public async Task Past_periods_agree_before_and_after_a_return_of_that_period_is_cancelled()
    {
        await using var host = await FarmingHost.StartAsync();
        var world = await host.WorldAsync();
        var owner = world.Owner;
        var tag = NewCode();
        world = world with { SupplierId = await owner.CreateSupplierAsync(name: $"{tag} NCC") };
        var receipt = await owner.CreateReceiptAsync(world.Body(world.MaterialForRaft(10, 10_000)) with { ReceiptDate = Today.AddDays(-60), Complete = true });
        var returned = await world.ReturnAsync(receipt.Id(), receipt.LineOf(world.Material), 3);
        var returnId = Newest(returned);
        var day = Today.AddDays(-40);
        await using (var db = host.Database.CreateContext())
        {
            await db.Database.ExecuteSqlAsync($"UPDATE PurchaseReturns SET ReturnDate = {day} WHERE Id = {returnId}");
        }

        async Task<(decimal Opening, decimal Closing, int Entries)> StatementAsync(DateOnly from, DateOnly to)
        {
            var s = await owner.GetFromJsonAsync<JsonElement>($"/api/supplier-debts/{world.SupplierId}/statement?from={from:yyyy-MM-dd}&to={to:yyyy-MM-dd}");
            return (s.Money("openingDebt"), s.Money("closingDebt"), s.GetProperty("entries").GetArrayLength());
        }

        async Task<(decimal Receipts, decimal Returns, decimal Net, decimal Raft)> TotalsAsync(DateOnly from, DateOnly to)
        {
            var t = (await owner.GetFromJsonAsync<JsonElement>($"/api/purchase-receipts?supplierId={world.SupplierId}&raftId={world.Raft.Id}&from={from:yyyy-MM-dd}&to={to:yyyy-MM-dd}")).GetProperty("totals");
            return (t.Money("receiptsTotal"), t.Money("returnsTotal"), t.Money("netTotal"), t.Money("raftMaterialCost"));
        }

        async Task<decimal> ExportedReturnsAsync(DateOnly from, DateOnly to)
        {
            var file = await owner.GetAsync($"/api/purchase-receipts/export?supplierId={world.SupplierId}&from={from:yyyy-MM-dd}&to={to:yyyy-MM-dd}");
            Assert.Equal(HttpStatusCode.OK, file.StatusCode);
            using var workbook = new XLWorkbook(await file.Content.ReadAsStreamAsync());
            return workbook.Worksheet("Purchase receipts").RowsUsed().Single(r => r.Cell(1).GetString() == "Purchase returns in the period").Cell(6).GetValue<decimal>();
        }

        var (before, inside, after) = (day.AddDays(-1), (From: day.AddDays(-5), To: day.AddDays(5)), day.AddDays(6));

        Assert.Equal((100_000m, 70_000m, 1), await StatementAsync(inside.From, inside.To));
        Assert.Equal((70_000m, 70_000m, 0), await StatementAsync(after, Today));
        Assert.Equal((100_000m, 100_000m, 0), await StatementAsync(before.AddDays(-2), before));
        Assert.Equal((0m, 30_000m, -30_000m, -30_000m), await TotalsAsync(inside.From, inside.To));
        Assert.Equal(30_000m, await ExportedReturnsAsync(inside.From, inside.To));
        var costs = await owner.GetFromJsonAsync<JsonElement>($"/api/purchase-receipts/raft-material-costs?raftId={world.Raft.Id}&from={inside.From:yyyy-MM-dd}&to={inside.To:yyyy-MM-dd}");
        Assert.Equal((-30_000m, 1), (costs.Money("materialCost"), costs.GetProperty("rafts")[0].GetProperty("returns").GetArrayLength()));
        Assert.Equal(70_000m, (await owner.GetFromJsonAsync<JsonElement>($"/api/rafts/{world.Raft.Id}")).Money("materialCost"));

        await (await owner.CancelReturnAsync(returnId, "Not sent back")).ReadAsync(HttpStatusCode.OK);

        Assert.Equal((100_000m, 100_000m, 0), await StatementAsync(inside.From, inside.To));
        Assert.Equal((100_000m, 100_000m, 0), await StatementAsync(after, Today));
        Assert.Equal((0m, 0m, 0m, 0m), await TotalsAsync(inside.From, inside.To));
        Assert.Equal(0m, await ExportedReturnsAsync(inside.From, inside.To));
        var costsAfter = await owner.GetFromJsonAsync<JsonElement>($"/api/purchase-receipts/raft-material-costs?raftId={world.Raft.Id}&from={inside.From:yyyy-MM-dd}&to={inside.To:yyyy-MM-dd}");
        Assert.Empty(costsAfter.GetProperty("rafts").EnumerateArray());
        Assert.Equal(100_000m, (await owner.GetFromJsonAsync<JsonElement>($"/api/rafts/{world.Raft.Id}")).Money("materialCost"));
        var list = await (await owner.GetAsync($"/api/supplier-debts?search={tag}")).ReadAsync(HttpStatusCode.OK);
        Assert.Equal((100_000m, 60), (list.GetProperty("page").GetProperty("items")[0].Money("debt"), list.GetProperty("page").GetProperty("items")[0].GetProperty("debtAgeDays").GetInt32()));
    }

    // Parallel: the correction and the cancel of the only return read each other's rule. Whatever wins, nothing is left above the line and no answer is a server error.
    [SqlServerFact]
    public async Task Parallel_correction_return_and_cancel_of_a_return_never_leave_more_returned_than_the_line()
    {
        await using var host = await FarmingHost.StartAsync();
        var world = await host.WorldAsync();
        for (var round = 0; round < 5; round++)
        {
            var receipt = await world.CompletedAsync(new LineBody(world.Other, 10, 1_000));
            var line = receipt.LineOf(world.Other);
            var standing = Newest(await world.ReturnAsync(receipt.Id(), line, 2));

            var responses = await InParallelAsync(3, i => i switch
            {
                0 => world.Owner.CorrectAsync(receipt.Id(), "Counted again", new { lineId = line, quantity = 2 }),
                1 => world.Owner.PostReturnAsync(receipt.Id(), new ReturnBody { Lines = [new ReturnLineBody(line, 8)] }),
                _ => world.Owner.CancelReturnAsync(standing, "Not sent back"),
            });

            Assert.All(responses, r => Assert.True((int)r.StatusCode < 500, $"round {round}: {(int)r.StatusCode}"));
            var quantity = await host.QueryAsync(db => db.PurchaseReceiptLines.Where(l => l.Id == line).Select(l => l.Quantity!.Value).SingleAsync());
            var recorded = await RecordedAsync(host, line);
            Assert.True(recorded <= quantity, $"round {round}: {recorded} returned of {quantity}");
        }
    }

    // Up with rows of the old model: each stands as Recorded, keeps its amount and its PRT code, and the refund leaves the debt (README: dropped on purpose, data is dev/demo only).
    // Down rounds each line on its own: 499 is gone as money, and the 400 + 400 of one return are 0, not 1,000.
    [SqlServerFact]
    public async Task The_migration_up_on_old_rows_and_down_on_lines_that_do_not_end_in_thousands()
    {
        await using var host = await FarmingHost.StartAsync();
        var world = await host.WorldAsync();
        var receipt = await world.CompletedAsync(new LineBody(world.Other, 1, 499), new LineBody(world.Material, 1, 500), new LineBody(world.Seed, 1, 999), new LineBody(world.Other, 1, 1_001));
        var lines = receipt.GetProperty("lines").EnumerateArray().OrderBy(l => l.GetProperty("lineNumber").GetInt32()).Select(l => l.Id()).ToList();
        var ids = new List<Guid>();
        foreach (var line in lines)
        {
            ids.Add(Newest(await world.ReturnAsync(receipt.Id(), line, 1)));
        }

        var split = await world.CompletedAsync(new LineBody(world.Other, 1, 400), new LineBody(world.Material, 1, 400));
        var both = await (await world.Owner.PostReturnAsync(split.Id(), new ReturnBody
        {
            Lines = split.GetProperty("lines").EnumerateArray().Select(l => new ReturnLineBody(l.Id(), 1)).ToList(),
        })).ReadAsync(HttpStatusCode.Created);
        var bothId = Newest(both);
        var old = await world.CompletedAsync(new LineBody(world.Other, 1, 25_000));
        var oldId = Newest(await world.ReturnAsync(old.Id(), old.LineOf(world.Other), 1));

        await using var db = host.Database.CreateContext();
        var migrator = db.GetService<IMigrator>();
        var all = db.Database.GetMigrations().ToList();
        await migrator.MigrateAsync(all[all.FindIndex(m => m.EndsWith("_PurchaseReturnsV2", StringComparison.Ordinal)) - 1]);

        async Task<decimal> OldColumnAsync(Guid id) => await db.Database.SqlQuery<decimal>($"SELECT DebtReductionAmount AS [Value] FROM PurchaseReturns WHERE Id = {id}").SingleAsync();
        Assert.Equal(new[] { 0m, 1_000m, 1_000m, 1_000m }, new[] { await OldColumnAsync(ids[0]), await OldColumnAsync(ids[1]), await OldColumnAsync(ids[2]), await OldColumnAsync(ids[3]) });
        Assert.Equal(0m, await OldColumnAsync(bothId));

        // A row as the old model wrote it: split into a refund and a debt reduction, with its old code.
        await db.Database.ExecuteSqlAsync($"UPDATE PurchaseReturns SET Code = 'PRT-20261101-001', RefundAmount = 7000, RefundMethod = 'Cash', DebtReductionAmount = 18000, EvidenceUrls = '[\"a.jpg\"]' WHERE Id = {oldId}");

        await migrator.MigrateAsync();

        var kept = await db.PurchaseReturns.AsNoTracking().Include(r => r.Lines).SingleAsync(r => r.Id == oldId);
        Assert.Equal((StatusIds.PurchaseReturn.Recorded, "PRT-20261101-001", 25_000m), (kept.StatusId, kept.Code, kept.Lines.Sum(l => l.ReturnValue)));
        var detail = await world.Owner.GetFromJsonAsync<JsonElement>($"/api/purchase-returns/{oldId}");
        Assert.Equal(("PRT-20261101-001", "Recorded", 25_000m), (detail.GetProperty("code").GetString(), detail.GetProperty("status").GetString(), detail.Money("returnValue")));
        // Receipts 2,999 + 800 + 25,000; returns as rounded by Down 0 + 1,000 + 1,000 + 1,000 + 0 + 0 + 25,000.
        Assert.Equal(28_799m - 28_000m, await world.DebtAsync());
        // A kept code can be cancelled like any other; the sequence goes on from where it was.
        await (await world.Owner.CancelReturnAsync(oldId, "Not sent back")).ReadAsync(HttpStatusCode.OK);
        Assert.Equal(25_799m, await world.DebtAsync());
    }
}
