using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using ClosedXML.Excel;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using OMTS.Api.IntegrationTests.Infrastructure;
using OMTS.Domain.Constants;
using OMTS.Domain.Enums;
using static OMTS.Api.IntegrationTests.Infrastructure.AcsTestKit;
using static OMTS.Api.IntegrationTests.Purchasing.PurchasingSqlServerKit;
using static OMTS.Api.IntegrationTests.Purchasing.PurchasingTestKit;
using FarmingHost = OMTS.Api.IntegrationTests.Farming.FarmingSqlServerHost;

namespace OMTS.Api.IntegrationTests.Purchasing;

/// <summary>PURCHASE-04/05 on a real, migrated SQL Server database: the sums, the statement, the export, parallel saves and the migration.</summary>
public class SupplierDebtSqlServerTests
{
    private const string Debts = "/api/supplier-debts";

    // The grouped SQL sums, with what must not count on the same supplier: Draft and Cancelled receipts, Cancelled payments.
    [SqlServerFact]
    public async Task The_sql_sums_list_detail_statement_and_export_agree_on_one_debt_with_dongs_and_a_return_with_a_refund()
    {
        await using var host = await FarmingHost.StartAsync();
        var world = await host.WorldAsync();
        var owner = world.Owner;
        var tag = NewCode();
        world = world with { SupplierId = await owner.CreateSupplierAsync(name: $"{tag} NCC") };

        var old = await owner.CreateReceiptAsync(world.Body(new LineBody(world.Seed, 12.5m, 45_501), new LineBody(world.Other, 1.5m, 333)) with { ReceiptDate = Today.AddDays(-20), Complete = true });
        Assert.Equal(568_763m + 500m, old.Money("total"));
        await world.PayAsync(100_001, Today.AddDays(-18));
        var recent = await owner.CreateReceiptAsync(world.Body(new LineBody(world.Other, 2, 250_000)) with { ReceiptDate = Today.AddDays(-5), Complete = true });
        await (await owner.PostReturnAsync(recent.Id(), new ReturnBody { RefundAmount = 25_000, RefundMethod = "Cash", DebtReductionAmount = 100_000, Lines = [new ReturnLineBody(recent.LineOf(world.Other), 0.5m)] }))
            .ReadAsync(HttpStatusCode.Created);
        await world.PayAsync(7, Today.AddDays(-3), "PaidBack");
        var cancelledPayment = await world.PayAsync(55_555);
        await (await owner.CancelPaymentAsync(cancelledPayment.Id(), "Mistake")).ReadAsync(HttpStatusCode.OK);
        var cancelledReceipt = await world.CompletedAsync(new LineBody(world.Other, 1, 999_000));
        await (await owner.CancelAsync(cancelledReceipt.Id(), "Wrong")).ReadAsync(HttpStatusCode.OK);
        await owner.CreateReceiptAsync(world.Body(new LineBody(world.Other, 1, 888_000)));

        // 569,263 + 500,000 − 125,000 + 25,000 − 100,001 + 7.
        const decimal debt = 869_269m;
        var list = await (await owner.GetAsync($"{Debts}?search={tag}")).ReadAsync(HttpStatusCode.OK);
        var row = Assert.Single(list.GetProperty("page").GetProperty("items").EnumerateArray());
        Assert.Equal((debt, 20), (row.Money("debt"), row.GetProperty("debtAgeDays").GetInt32()));

        var detail = await (await owner.GetAsync($"{Debts}/{world.SupplierId}")).ReadAsync(HttpStatusCode.OK);
        Assert.Equal(debt, detail.Money("debtToday"));
        // FIFO: the money held, 100,001 − 7 − 25,000, goes on the first receipt; a return of 125,000 lowers the second.
        Assert.Equal(
            [(old.Id(), 74_994m, 569_263m - 74_994m), (recent.Id(), 0m, 375_000m)],
            detail.GetProperty("unpaidReceipts").EnumerateArray().Select(r => (r.Id(), r.Money("covered"), r.Money("remaining"))).ToList());

        // Up to each day of a past period, the opening is what the sums said then.
        var before = await (await owner.GetAsync($"{Debts}/{world.SupplierId}/statement?from={Today.AddDays(-30):yyyy-MM-dd}&to={Today.AddDays(-6):yyyy-MM-dd}")).ReadAsync(HttpStatusCode.OK);
        Assert.Equal((0m, 569_263m - 100_001m), (before.Money("openingDebt"), before.Money("closingDebt")));
        var after = await (await owner.GetAsync($"{Debts}/{world.SupplierId}/statement?from={Today.AddDays(-5):yyyy-MM-dd}&to={Today:yyyy-MM-dd}")).ReadAsync(HttpStatusCode.OK);
        Assert.Equal((before.Money("closingDebt"), debt), (after.Money("openingDebt"), after.Money("closingDebt")));
        Assert.Equal(
            [("Receipt", 500_000m), ("PaidBack", 7m), ("Return", -125_000m), ("ReturnRefund", 25_000m)],
            after.GetProperty("entries").EnumerateArray().Select(e => (e.GetProperty("kind").GetString(), e.Money("amount"))).ToList());
        Assert.Equal(after.Money("openingDebt") + after.GetProperty("entries").EnumerateArray().Sum(e => e.Money("amount")), after.Money("closingDebt"));

        var export = await owner.GetAsync($"{Debts}/export?supplierId={world.SupplierId}&from={Today.AddDays(-30):yyyy-MM-dd}&to={Today:yyyy-MM-dd}&search={tag}");
        Assert.Equal(HttpStatusCode.OK, export.StatusCode);
        using var workbook = new XLWorkbook(await export.Content.ReadAsStreamAsync());
        Assert.Equal(debt, workbook.Worksheet("Supplier debts").RowsUsed().Skip(1).First().Cell(4).GetValue<decimal>());
        Assert.Equal(debt, workbook.Worksheet("Statement").RowsUsed().Last().Cell(9).GetValue<decimal>());

        // The cancel preview reads the same debt; a receipt with a return cannot be cancelled.
        var preview = await (await owner.GetAsync($"/api/purchase-receipts/{old.Id()}/cancel-preview")).ReadAsync(HttpStatusCode.OK);
        Assert.Equal((debt, debt - 569_263m), (preview.Money("debtBefore"), preview.Money("debtAfter")));
        // What leaves the debt with a receipt is its total less its returns, plus the money those brought back.
        var blocked = await (await owner.GetAsync($"/api/purchase-receipts/{recent.Id()}/cancel-preview")).ReadAsync(HttpStatusCode.OK);
        Assert.Equal(("Returned", debt - 400_000m), (blocked.GetProperty("blocked").GetProperty("kind").GetString(), blocked.Money("debtAfter")));
    }

    [SqlServerFact]
    public async Task Parallel_payments_each_get_their_own_TT_code_a_repeated_key_records_one_and_a_payment_is_cancelled_once()
    {
        await using var host = await FarmingHost.StartAsync();
        var world = await host.WorldAsync();
        var accountant = await host.StaffAsync(null, StaffRole.Accountant);

        var responses = await InParallelAsync(8, _ => accountant.PostPaymentAsync(new PaymentBody { SupplierId = world.SupplierId, Amount = 1_001, ConfirmAdvance = true }));

        Assert.All(responses, r => Assert.Equal(HttpStatusCode.Created, r.StatusCode));
        var codes = await host.QueryAsync(db => db.SupplierPayments.Where(p => p.SupplierId == world.SupplierId).Select(p => p.Code).ToListAsync());
        Assert.Equal(8, codes.Distinct().Count());
        Assert.All(codes, c => Assert.Matches("^TT\\d{6,}$", c));
        Assert.Equal(-8 * 1_001m, await world.DebtAsync());

        var key = $"retry-{Guid.NewGuid():N}";
        var keyed = await InParallelAsync(5, _ => accountant.PostPaymentAsync(new PaymentBody { SupplierId = world.SupplierId, Amount = 100_000, ConfirmAdvance = true, DeduplicationKey = key }));
        var ids = new HashSet<Guid>();
        foreach (var response in keyed)
        {
            ids.Add((await response.ReadAsync(HttpStatusCode.Created)).Id());
        }

        var saved = Assert.Single(ids);
        Assert.Equal(1, await host.QueryAsync(db => db.SupplierPayments.CountAsync(p => p.DeduplicationKey == key)));
        // The Owner hears of it once, whatever the retries.
        var told = await host.QueryAsync(db => db.Notifications.Where(n => n.RelatedRecordId == saved).Select(n => n.RecipientAccountId).ToListAsync());
        Assert.Equal(told.Distinct().Count(), told.Count);

        var cancels = await InParallelAsync(4, _ => accountant.CancelPaymentAsync(saved, "Mistake"));
        Assert.Single(cancels, r => r.StatusCode == HttpStatusCode.OK);
        foreach (var loser in cancels.Where(r => r.StatusCode != HttpStatusCode.OK))
        {
            Assert.Contains(await loser.CodeOrStatusAsync(), new[] { "409 MSG06", "409 MSG-GEN-09" });
        }

        Assert.Equal(1, await host.QueryAsync(db => db.AuditEvents.CountAsync(e => e.RecordId == saved && e.Action == "SupplierPayment.Cancelled")));
    }

    [SqlServerFact]
    public async Task A_cancel_with_a_row_version_from_an_old_screen_answers_MSG06_and_keeps_the_payment()
    {
        await using var host = await FarmingHost.StartAsync();
        var world = await host.WorldAsync();
        var payment = await world.PayAsync(1_000);

        var stale = await world.Owner.PostAsJsonAsync($"/api/supplier-payments/{payment.Id()}/cancel", new { reason = "Late", rowVersion = Convert.ToBase64String([0, 0, 0, 0, 0, 0, 0, 1]) });
        await stale.ProblemAsync(HttpStatusCode.Conflict, "MSG06");

        Assert.Equal(StatusIds.SupplierPayment.Recorded, await host.QueryAsync(db => db.SupplierPayments.Where(p => p.Id == payment.Id()).Select(p => p.StatusId).SingleAsync()));
        await (await world.Owner.PostAsJsonAsync($"/api/supplier-payments/{payment.Id()}/cancel", new { reason = "Now", rowVersion = payment.GetProperty("rowVersion").GetString() })).ReadAsync(HttpStatusCode.OK);
    }

    [SqlServerFact]
    public async Task Longest_allowed_values_of_a_payment_fit_the_columns()
    {
        await using var host = await FarmingHost.StartAsync();
        var world = await host.WorldAsync();

        var payment = await (await world.Owner.PostPaymentAsync(new PaymentBody
        {
            SupplierId = world.SupplierId, Amount = 999_999_999_999_999_999m, Method = "BankTransfer", ConfirmAdvance = true,
            BankReference = new string('ế', 100), Note = new string('ỷ', 1000), DeduplicationKey = new string('k', 90) + NewCode()[..10],
        })).ReadAsync(HttpStatusCode.Created);
        Assert.Equal(999_999_999_999_999_999m, payment.Money("amount"));

        await (await world.Owner.CancelPaymentAsync(payment.Id(), new string('ề', 1000))).ReadAsync(HttpStatusCode.OK);
    }

    // Migration SupplierPaymentsV2 on rows of the old model: each gets a TT code in payment order, the counter follows them;
    // down (lossy) drops money paid back, rounds to thousands and leaves the payments detached.
    [SqlServerFact]
    public async Task SupplierPaymentsV2_goes_down_and_up_again_giving_old_rows_TT_codes_in_payment_order_and_starting_the_counter_after_them()
    {
        await using var host = await FarmingHost.StartAsync();
        var world = await host.WorldAsync();
        var newest = await world.PayAsync(1_500, Today);
        var oldest = await world.PayAsync(2_000, Today.AddDays(-5));
        await world.PayAsync(700, kind: "PaidBack");

        await using var db = host.Database.CreateContext();
        var migrator = db.GetService<IMigrator>();
        var all = db.Database.GetMigrations().ToList();
        await migrator.MigrateAsync(all[all.FindIndex(m => m.EndsWith("_SupplierPaymentsV2", StringComparison.Ordinal)) - 1]);

        Assert.Equal(2, await db.Database.SqlQuery<int>($"SELECT COUNT(*) AS [Value] FROM SupplierPayments WHERE SupplierId = {world.SupplierId} AND Amount % 1000 = 0 AND ReceiptId IS NULL").SingleAsync());
        Assert.Equal(0, await db.Database.SqlQuery<int>($"SELECT COUNT(*) AS [Value] FROM DocumentSequences WHERE [Key] = 'TT'").SingleAsync());
        var before = await db.Database.SqlQuery<int>($"SELECT COUNT(*) AS [Value] FROM SupplierPayments").SingleAsync();

        await migrator.MigrateAsync();

        Assert.Empty(await db.Database.GetPendingMigrationsAsync());
        Assert.False(db.Database.HasPendingModelChanges());
        var rows = await db.SupplierPayments.AsNoTracking().Where(p => p.SupplierId == world.SupplierId).OrderBy(p => p.PaymentDate).ToListAsync();
        Assert.Equal([oldest.Id(), newest.Id()], rows.Select(p => p.Id));
        Assert.All(rows, p => Assert.Equal(SupplierPaymentKind.Payment, p.Kind));
        Assert.All(rows, p => Assert.Matches("^TT\\d{6}$", p.Code));
        Assert.True(string.CompareOrdinal(rows[0].Code, rows[1].Code) < 0);
        Assert.Equal(before, await db.DocumentSequences.Where(s => s.Key == "TT").Select(s => s.LastValue).SingleAsync());

        var next = await world.PayAsync(1_000);
        Assert.Equal(OMTS.Application.Features.Purchasing.DocumentCodes.PaymentCode(before + 1), next.GetProperty("code").GetString());
    }
}
