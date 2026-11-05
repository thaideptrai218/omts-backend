using System.Net;
using System.Text.Json;
using ClosedXML.Excel;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.DependencyInjection;
using OMTS.Api.IntegrationTests.Infrastructure;
using OMTS.Application.Common.Interfaces.Persistence;
using OMTS.Application.Features.Notifications;
using OMTS.Application.Features.Purchasing.Payments;
using OMTS.Domain.Constants;
using OMTS.Domain.Enums;
using OMTS.Infrastructure.Messaging.Fakes;
using static OMTS.Api.IntegrationTests.Infrastructure.AcsTestKit;
using static OMTS.Api.IntegrationTests.Purchasing.PurchasingSqlServerKit;
using static OMTS.Api.IntegrationTests.Purchasing.PurchasingTestKit;
using FarmingHost = OMTS.Api.IntegrationTests.Farming.FarmingSqlServerHost;

namespace OMTS.Api.IntegrationTests.Purchasing;

/// <summary>
/// QA of PURCHASE-04/05 on a real, migrated SQL Server database (report qa-261104-supplier-payments-debts.md). A test whose
/// comment names a FINDING is a defect accepted for now, and skipped; the others pin what was checked, found right or fixed.
/// </summary>
public class SupplierPaymentsDebtsQaSqlServerTests
{
    private const string Debts = "/api/supplier-debts";

    // Awkward data on one supplier, by hand: half-up lines, a zero-priced line and receipt, two receipts on a day, an advance older
    // than every receipt, a 1-dong payment, money paid back, a return with a refund, and what must not count.
    // List, detail, statement and export must agree, and the pure ledger must agree with the SQL sums on every day.
    [SqlServerFact]
    public async Task List_detail_statement_export_pure_ledger_and_sql_sums_agree_on_awkward_data()
    {
        await using var host = await FarmingHost.StartAsync();
        var world = await host.WorldAsync();
        var owner = world.Owner;
        var tag = NewCode();
        world = world with { SupplierId = await owner.CreateSupplierAsync(name: $"{tag} NCC") };
        ReceiptBody Receipt(int daysAgo, bool complete, params LineBody[] lines) => world.Body(lines) with { ReceiptDate = Today.AddDays(-daysAgo), Complete = complete };

        // 568,762.5 → 568,763; 499.5 → 500; 330.33 → 330; 0: the total is the sum of the rounded lines, 569,593.
        var first = await owner.CreateReceiptAsync(Receipt(40, true,
            new LineBody(world.Seed, 12.5m, 45_501), new LineBody(world.Other, 1.5m, 333), new LineBody(world.Material, 0.33m, 1_001), new LineBody(world.Other, 5, 0)));
        Assert.Equal(569_593m, first.Money("total"));
        var second = await owner.CreateReceiptAsync(Receipt(40, true, new LineBody(world.Other, 1, 1_000_000)));
        await owner.CreateReceiptAsync(Receipt(39, true, new LineBody(world.Other, 1, 0)));
        var today = await owner.CreateReceiptAsync(Receipt(0, true, new LineBody(world.Other, 3, 333_334)));

        await world.PayAsync(200_001, Today.AddDays(-100));
        await world.PayAsync(1, Today.AddDays(-39));
        await world.PayAsync(7, Today.AddDays(-38), "PaidBack");
        var mistake = await world.PayAsync(55_555, Today.AddDays(-37));
        await (await owner.CancelPaymentAsync(mistake.Id(), "Mistake")).ReadAsync(HttpStatusCode.OK);
        var mistakeBack = await world.PayAsync(44_444, Today.AddDays(-37), "PaidBack");
        await (await owner.CancelPaymentAsync(mistakeBack.Id(), "Mistake")).ReadAsync(HttpStatusCode.OK);
        var wrong = await owner.CreateReceiptAsync(Receipt(36, true, new LineBody(world.Other, 1, 999_000)));
        await (await owner.CancelAsync(wrong.Id(), "Wrong")).ReadAsync(HttpStatusCode.OK);
        await owner.CreateReceiptAsync(Receipt(35, false, new LineBody(world.Other, 1, 888_000)));
        await (await owner.PostReturnAsync(second.Id(), new ReturnBody
        {
            ReturnDate = Today.AddDays(-30), RefundAmount = 25_000, RefundMethod = "Cash", DebtReductionAmount = 225_000, Lines = [new ReturnLineBody(second.LineOf(world.Other), 0.25m)],
        })).ReadAsync(HttpStatusCode.Created);
        await (await owner.PostPaymentAsync(new PaymentBody { SupplierId = world.SupplierId, Amount = 77_777, Method = "BankTransfer", BankReference = $"FT-{tag}" })).ReadAsync(HttpStatusCode.Created);

        // 569,593 + 1,000,000 + 0 + 1,000,002 − 250,000 + 25,000 − 200,001 − 1 + 7 − 77,777.
        const decimal debt = 2_066_823m;
        var list = await (await owner.GetAsync($"{Debts}?search={tag}")).ReadAsync(HttpStatusCode.OK);
        var row = Assert.Single(list.GetProperty("page").GetProperty("items").EnumerateArray());
        Assert.Equal((debt, 40, debt, 0m), (row.Money("debt"), row.GetProperty("debtAgeDays").GetInt32(), list.Money("totalOwed"), list.Money("totalAdvance")));

        // FIFO: 200,002 + 77,777 − 7 − 25,000 = 252,772 held; it goes on the first receipt of the oldest day.
        var detail = await (await owner.GetAsync($"{Debts}/{world.SupplierId}")).ReadAsync(HttpStatusCode.OK);
        Assert.Equal((debt, 40), (detail.Money("debtToday"), detail.GetProperty("debtAgeDays").GetInt32()));
        Assert.Equal(
            [(first.Id(), 252_772m, 316_821m, 40), (second.Id(), 0m, 750_000m, 40), (today.Id(), 0m, 1_000_002m, 0)],
            detail.GetProperty("unpaidReceipts").EnumerateArray().Select(r => (r.Id(), r.Money("covered"), r.Money("remaining"), r.GetProperty("ageDays").GetInt32())).ToList());
        Assert.Equal(debt, detail.GetProperty("unpaidReceipts").EnumerateArray().Sum(r => r.Money("remaining")));

        var from = Today.AddDays(-200);
        var whole = await (await owner.GetAsync($"{Debts}/{world.SupplierId}/statement?from={from:yyyy-MM-dd}&to={Today:yyyy-MM-dd}")).ReadAsync(HttpStatusCode.OK);
        var entries = whole.GetProperty("entries").EnumerateArray().ToList();
        Assert.Equal((0m, debt, debt), (whole.Money("openingDebt"), whole.Money("closingDebt"), whole.Money("debtToday")));
        Assert.Equal(whole.Money("closingDebt"), whole.Money("openingDebt") + entries.Sum(e => e.Money("amount")));
        Assert.Equal(debt, entries[^1].Money("debtAfter"));
        Assert.DoesNotContain(entries, e => e.Id() == mistake.Id() || e.Id() == mistakeBack.Id() || e.Id() == wrong.Id());

        // Any cut gives closing = next opening, and the SQL sums at any day equal the running debt of the pure ledger.
        foreach (var cut in new[] { -101, -100, -40, -39, -38, -37, -30, -1 })
        {
            var left = await (await owner.GetAsync($"{Debts}/{world.SupplierId}/statement?from={from:yyyy-MM-dd}&to={Today.AddDays(cut):yyyy-MM-dd}")).ReadAsync(HttpStatusCode.OK);
            var right = await (await owner.GetAsync($"{Debts}/{world.SupplierId}/statement?from={Today.AddDays(cut + 1):yyyy-MM-dd}&to={Today:yyyy-MM-dd}")).ReadAsync(HttpStatusCode.OK);
            Assert.Equal(left.Money("closingDebt"), right.Money("openingDebt"));
            Assert.Equal(debt, right.Money("openingDebt") + right.GetProperty("entries").EnumerateArray().Sum(e => e.Money("amount")));
        }

        using (var scope = host.Api.Services.CreateScope())
        {
            var queries = scope.ServiceProvider.GetRequiredService<ISupplierDebtQuery>();
            var (receipts, returns, payments) = await queries.StatementRowsAsync(world.SupplierId, from, Today, CancellationToken.None);
            var ledger = SupplierDebtLedger.Statement(0, receipts, returns, payments);
            foreach (var day in ledger.GroupBy(e => e.Date))
            {
                var sums = await queries.SumsAsync(world.SupplierId, day.Key, CancellationToken.None);
                Assert.Equal(day.Last().DebtAfter, sums[world.SupplierId].Debt);
            }

            var all = (await queries.SumsAsync(null, Today, CancellationToken.None))[world.SupplierId];
            var one = (await queries.SumsAsync(world.SupplierId, Today, CancellationToken.None))[world.SupplierId];
            Assert.Equal((debt, one), (all.Debt, all));
            Assert.Equal(debt, SupplierDebtLedger.UnpaidReceipts(await queries.ReceiptsAsync([world.SupplierId], CancellationToken.None), all.Credit, Today).Sum(r => r.Remaining));
        }

        var export = await owner.GetAsync($"{Debts}/export?supplierId={world.SupplierId}&from={from:yyyy-MM-dd}&to={Today:yyyy-MM-dd}&search={tag}");
        Assert.Equal(HttpStatusCode.OK, export.StatusCode);
        using var workbook = new XLWorkbook(await export.Content.ReadAsStreamAsync());
        var sheet = workbook.Worksheet("Supplier debts").RowsUsed().Skip(1).ToList();
        Assert.Equal((debt, 40m), (sheet[0].Cell(4).GetValue<decimal>(), sheet[0].Cell(5).GetValue<decimal>()));
        Assert.Equal(("Total owed", debt, "Total paid in advance", 0m), (sheet[1].Cell(1).GetString(), sheet[1].Cell(4).GetValue<decimal>(), sheet[2].Cell(1).GetString(), sheet[2].Cell(4).GetValue<decimal>()));
        var statement = workbook.Worksheet("Statement").RowsUsed().Skip(1).ToList();
        Assert.Equal(entries.Count + 2, statement.Count);
        Assert.Equal(entries.Select(e => e.Money("debtAfter")), statement.Skip(1).SkipLast(1).Select(r => r.Cell(9).GetValue<decimal>()));
        Assert.Equal(debt, statement[^1].Cell(9).GetValue<decimal>());
    }

    // MSG-PURCHASE-04 carries {debt_amount} "after it" (BR-PURCHASE-05, client list §6), and so does debtAfter. Of the parallel payments
    // the ones that succeed each tell a debt of their own, in the answer and to the Owner; the others answer MSG06 and save nothing.
    [SqlServerFact]
    public async Task Parallel_payments_that_succeed_each_tell_a_distinct_true_debt_the_others_answer_MSG06_and_the_stored_debt_agrees()
    {
        await using var host = await FarmingHost.StartAsync();
        var world = await host.WorldAsync();
        var accountant = await host.StaffAsync(null, StaffRole.Accountant);
        await world.CompletedAsync(new LineBody(world.Other, 1, 1_000_000));

        var responses = await InParallelAsync(6, _ => accountant.PostPaymentAsync(new PaymentBody { SupplierId = world.SupplierId, Amount = 100_000 }));

        var saved = new List<JsonElement>();
        foreach (var response in responses)
        {
            if (response.StatusCode == HttpStatusCode.Created)
            {
                saved.Add(await response.ReadAsync(HttpStatusCode.Created));
            }
            else
            {
                await response.ProblemAsync(HttpStatusCode.Conflict, "MSG06");
            }
        }

        Assert.NotEmpty(saved);
        var expected = Enumerable.Range(1, saved.Count).Select(i => 1_000_000m - (i * 100_000m)).ToList();
        Assert.Equal(expected, saved.Select(p => p.Money("debtAfter")).OrderDescending());
        Assert.Equal(expected[^1], await world.DebtAsync());
        Assert.Equal(saved.Count, await host.QueryAsync(db => db.SupplierPayments.CountAsync(p => p.SupplierId == world.SupplierId)));

        // The Owner is told what each answer said, and only of what was saved.
        var ids = saved.Select(p => p.Id()).ToList();
        var told = await host.QueryAsync(db => db.Notifications
            .Where(n => n.Type == NotificationTypes.SupplierTransaction && n.RecipientAccountId == host.Owner.Account.Id)
            .Select(n => new { n.RelatedRecordId, n.MessageParams }).ToListAsync());
        Assert.Equal(ids.Order(), told.Select(n => n.RelatedRecordId!.Value).Order());
        Assert.Equal(expected, told.Select(n => JsonDocument.Parse(n.MessageParams!).RootElement.GetProperty("debt_amount").GetDecimal()).OrderDescending());
    }

    // A payment and a cancel at once, a double cancel and a repeated key, each sent again on MSG06 as a client does: no server error, the debt is right, one row and one push each.
    [SqlServerFact]
    public async Task Payments_cancels_and_retries_at_once_leave_the_right_debt_one_notification_and_one_push_each()
    {
        await using var host = await FarmingHost.StartAsync();
        var world = await host.WorldAsync();
        var accountant = await host.StaffAsync(null, StaffRole.Accountant);
        await world.CompletedAsync(new LineBody(world.Other, 1, 1_000_000));
        var toCancel = new List<JsonElement>();
        for (var i = 0; i < 3; i++)
        {
            toCancel.Add(await world.PayAsync(10_000));
        }

        var key = $"retry-{Guid.NewGuid():N}";
        var requests = new List<Func<Task<HttpResponseMessage>>>();
        foreach (var payment in toCancel)
        {
            requests.Add(() => accountant.CancelPaymentAsync(payment.Id(), "Mistake"));
            requests.Add(() => world.Owner.CancelPaymentAsync(payment.Id(), "Twice"));
        }

        for (var i = 0; i < 3; i++)
        {
            requests.Add(() => accountant.PostPaymentAsync(new PaymentBody { SupplierId = world.SupplierId, Amount = 1_000 }));
            requests.Add(() => accountant.PostPaymentAsync(new PaymentBody { SupplierId = world.SupplierId, Amount = 50_000, DeduplicationKey = key }));
        }

        var responses = await InParallelAsync(requests.Count, i => AgainOnConflictAsync(requests[i]));

        foreach (var loser in responses.Where(r => !r.IsSuccessStatusCode))
        {
            Assert.Equal("409 MSG-GEN-09", await loser.CodeOrStatusAsync());
        }

        var state = await host.QueryAsync(db => db.SupplierPayments.Where(p => p.SupplierId == world.SupplierId)
            .Select(p => new { p.Id, p.Code, p.Kind, p.Amount, p.StatusId, p.DeduplicationKey }).ToListAsync());
        Assert.All(toCancel, c => Assert.Equal(StatusIds.SupplierPayment.Cancelled, state.Single(s => s.Id == c.Id()).StatusId));
        var keyed = Assert.Single(state, s => s.DeduplicationKey == key);
        Assert.Equal(3, state.Count(s => s.Amount == 1_000));
        Assert.Equal(1_000_000m - 50_000m - 3_000m, await world.DebtAsync());
        Assert.Equal(state.Count, state.Select(s => s.Code).Distinct().Count());

        var rows = await host.QueryAsync(db => db.Notifications.Where(n => n.Type == NotificationTypes.SupplierTransaction).Select(n => new { n.RelatedRecordId, n.RecipientAccountId, n.MessageParams }).ToListAsync());
        var mine = rows.Where(n => n.RecipientAccountId == host.Owner.Account.Id).ToList();
        // A cancelled payment: its record and its cancellation. The retried key: its record once.
        Assert.All(toCancel, c => Assert.Equal(2, mine.Count(n => n.RelatedRecordId == c.Id())));
        Assert.Single(mine, n => n.RelatedRecordId == keyed.Id);
        var pushes = host.Api.Services.GetRequiredService<FakePushSender>().Inbox.List(host.Owner.Account.Id.ToString());
        Assert.Single(pushes, p => p.MessageParams is { } values && values.TryGetProperty("payment_code", out var code) && code.GetString() == keyed.Code);
    }

    // Allocated only once the payment is valid and confirmed; a refused request spends no number; bank references compare as SQL Server does.
    [SqlServerFact]
    public async Task A_refused_payment_spends_no_TT_number_and_a_reference_differing_in_case_or_spaces_is_the_same_reference()
    {
        await using var host = await FarmingHost.StartAsync();
        var world = await host.WorldAsync();
        await (await world.Owner.PostPaymentAsync(new PaymentBody { SupplierId = world.SupplierId, Amount = 1, ConfirmAdvance = true, Method = "BankTransfer", BankReference = "FT-AbC" })).ReadAsync(HttpStatusCode.Created);
        Task<long> LastAsync() => host.QueryAsync(db => db.DocumentSequences.Where(s => s.Key == "TT").Select(s => s.LastValue).SingleAsync());
        var before = await LastAsync();

        await (await world.Owner.PostPaymentAsync(new PaymentBody { SupplierId = world.SupplierId, Amount = 1 })).ProblemAsync(HttpStatusCode.Conflict, "MSG-PURCHASE-05");
        await (await world.Owner.PostPaymentAsync(new PaymentBody { SupplierId = world.SupplierId, Amount = 0, ConfirmAdvance = true })).ProblemAsync(HttpStatusCode.BadRequest, "MSG-GEN-01");
        foreach (var reference in new[] { "FT-AbC", "ft-abc", "  FT-ABC  " })
        {
            await (await world.Owner.PostPaymentAsync(new PaymentBody { SupplierId = world.SupplierId, Amount = 1, ConfirmAdvance = true, Method = "BankTransfer", BankReference = reference })).ProblemAsync(HttpStatusCode.Conflict, "MSG-PURCHASE-06");
        }

        Assert.Equal(before, await LastAsync());
    }

    // The ends of what a date can hold reach SQL Server, on the screen and in the file (M1: Excel has no such early dates, so they are text).
    [SqlServerFact]
    public async Task The_oldest_and_latest_dates_a_statement_and_its_export_can_ask_for_work_on_sql_server()
    {
        await using var host = await FarmingHost.StartAsync();
        var world = await host.WorldAsync();
        Assert.Equal(HttpStatusCode.OK, (await world.Owner.GetAsync($"{Debts}/export?supplierId={world.SupplierId}&from=9999-12-01&to=9999-12-31")).StatusCode);
        await world.PayAsync(5, new DateOnly(1, 1, 1));
        await world.PayAsync(7, new DateOnly(1, 1, 2), "PaidBack");

        var oldest = await (await world.Owner.GetAsync($"{Debts}/{world.SupplierId}/statement?from=0001-01-01&to=0001-01-02")).ReadAsync(HttpStatusCode.OK);
        Assert.Equal((0m, -5m + 7m), (oldest.Money("openingDebt"), oldest.Money("closingDebt")));
        var next = await (await world.Owner.GetAsync($"{Debts}/{world.SupplierId}/statement?from=0001-01-02&to=0001-01-02")).ReadAsync(HttpStatusCode.OK);
        Assert.Equal((-5m, 2m), (next.Money("openingDebt"), next.Money("closingDebt")));
        var latest = await (await world.Owner.GetAsync($"{Debts}/{world.SupplierId}/statement?from=9999-12-01&to=9999-12-31")).ReadAsync(HttpStatusCode.OK);
        Assert.Equal((2m, 2m), (latest.Money("openingDebt"), latest.Money("closingDebt")));

        var export = await world.Owner.GetAsync($"{Debts}/export?supplierId={world.SupplierId}&from=0001-01-01&to=0001-01-02");
        Assert.Equal(HttpStatusCode.OK, export.StatusCode);
        using var workbook = new XLWorkbook(await export.Content.ReadAsStreamAsync());
        Assert.Equal(
            ["01/01/0001 00:00", "01/01/0001 00:00", "02/01/0001 00:00", "02/01/0001 00:00"],
            workbook.Worksheet("Statement").RowsUsed().Skip(1).Select(r => r.Cell(1).GetString()));
        Assert.Equal(-5m + 7m, workbook.Worksheet("Statement").RowsUsed().Last().Cell(9).GetValue<decimal>());
    }

    // Rows of the old model: linked to a receipt, a reference, evidence, method Other, one Cancelled, two on one date.
    [SqlServerFact]
    public async Task Up_keeps_old_rows_with_their_reference_status_and_method_in_payment_order_and_they_count_as_the_new_rules_say()
    {
        await using var host = await FarmingHost.StartAsync();
        var world = await host.WorldAsync();
        var receipt = await world.CompletedAsync(new LineBody(world.Other, 1, 10_000_000));
        var third = await world.PayAsync(3_000, Today);
        var second = await world.PayAsync(2_000, Today.AddDays(-1));
        var first = await world.PayAsync(1_000, Today.AddDays(-2));
        var cancelled = await world.PayAsync(4_000, Today.AddDays(-1));

        await using var db = host.Database.CreateContext();
        var migrator = db.GetService<IMigrator>();
        var all = db.Database.GetMigrations().ToList();
        await migrator.MigrateAsync(all[all.FindIndex(m => m.EndsWith("_SupplierPaymentsV2", StringComparison.Ordinal)) - 1]);

        // Same date: the older CreatedAt comes first.
        await db.Database.ExecuteSqlAsync($"UPDATE SupplierPayments SET ReceiptId = {receipt.Id()}, ReferenceCode = N'OLD-REF', EvidenceUrls = N'[\"a.jpg\"]', Method = N'Other' WHERE Id = {second.Id()}");
        await db.Database.ExecuteSqlAsync($"UPDATE SupplierPayments SET CreatedAt = DATEADD(DAY, -1, CreatedAt) WHERE Id = {second.Id()}");
        await db.Database.ExecuteSqlAsync($"UPDATE SupplierPayments SET StatusId = {StatusIds.SupplierPayment.Cancelled}, CancelReason = N'Old reason' WHERE Id = {cancelled.Id()}");

        await migrator.MigrateAsync();

        var rows = await db.SupplierPayments.AsNoTracking().Where(p => p.SupplierId == world.SupplierId).ToListAsync();
        var codes = new[] { first, second, cancelled, third }.Select(p => rows.Single(r => r.Id == p.Id()).Code).ToList();
        Assert.Equal(codes.Order(StringComparer.Ordinal), codes);
        var migrated = rows.Single(r => r.Id == second.Id());
        Assert.Equal(("OLD-REF", PaymentMethod.Other, SupplierPaymentKind.Payment), (migrated.BankReference, migrated.Method, migrated.Kind));
        Assert.Equal("Old reason", rows.Single(r => r.Id == cancelled.Id()).CancelReason);

        // 10,000,000 − 1,000 − 2,000 − 3,000, the Cancelled row out.
        Assert.Equal(9_994_000m, await world.DebtAsync());
        var view = await (await world.Owner.GetAsync($"/api/supplier-payments/{second.Id()}")).ReadAsync(HttpStatusCode.OK);
        Assert.Equal("Other", view.GetProperty("method").GetString());
        await (await world.Owner.CancelPaymentAsync(second.Id(), "Now")).ReadAsync(HttpStatusCode.OK);
        var existing = rows.Select(r => r.Code).Concat(await db.SupplierPayments.AsNoTracking().Select(p => p.Code).ToListAsync()).Max(StringComparer.Ordinal)!;
        var next = (await world.PayAsync(1)).GetProperty("code").GetString()!;
        Assert.True(string.CompareOrdinal(next, existing) > 0, $"{next} after {existing}");
    }

    // FINDING L6, accepted. Down should work on any data the Up model allows. ROUND on the largest amount the API accepts (SupplierPaymentRules.MaxAmount) overflows decimal(18,0).
    [SqlServerFact(Skip = "Accepted: Down on the largest accepted amount, dev data only")]
    public async Task Finding_L6_down_works_with_the_largest_amount_the_api_accepts()
    {
        await using var host = await FarmingHost.StartAsync();
        var world = await host.WorldAsync();
        await world.PayAsync(999_999_999_999_999_999m);

        await using var db = host.Database.CreateContext();
        var migrator = db.GetService<IMigrator>();
        var all = db.Database.GetMigrations().ToList();
        var exception = await Record.ExceptionAsync(() => migrator.MigrateAsync(all[all.FindIndex(m => m.EndsWith("_SupplierPaymentsV2", StringComparison.Ordinal)) - 1]));

        Assert.Null(exception);
    }
}
