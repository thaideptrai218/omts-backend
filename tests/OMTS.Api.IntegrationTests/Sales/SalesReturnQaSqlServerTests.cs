using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.DependencyInjection;
using OMTS.Api.IntegrationTests.Cultivation;
using OMTS.Api.IntegrationTests.Farming;
using OMTS.Api.IntegrationTests.Harvest;
using OMTS.Api.IntegrationTests.Infrastructure;
using OMTS.Api.IntegrationTests.Traceability;
using OMTS.Application.Features.Sales.QualityClaims;
using OMTS.Domain.Constants;
using OMTS.Infrastructure.Persistence;
using static OMTS.Api.IntegrationTests.Farming.FarmingTestKit;
using static OMTS.Api.IntegrationTests.Purchasing.PurchasingSqlServerKit;
using static OMTS.Api.IntegrationTests.Sales.SalesCatalogueTestKit;
using static OMTS.Api.IntegrationTests.Sales.SalesInvoiceSendingTestKit;
using static OMTS.Api.IntegrationTests.Sales.SalesInvoiceTestKit;
using static OMTS.Api.IntegrationTests.Sales.SalesReturnTestKit;

namespace OMTS.Api.IntegrationTests.Sales;

/// <summary>QA of CLAIM-03 on a real SQL Server (OMTS-225): the migration, every read that joins the returns, column sizes and the races the author's tests leave out.</summary>
public class SalesReturnQaSqlServerTests
{
    // Migrations of other branches sort before and after it, so the test compares this step alone: its predecessor in the chain, and itself.
    private const string ReturnsMigration = "20261109133559_SalesReturns";

    private sealed record World(FarmingSqlServerHost Host, HttpClient Owner, HttpClient Manager, TraderActor Trader, Guid ZoneId, Guid ProductId, Guid[] LotIds);

    private static async Task<World> StartAsync(int lots = 2)
    {
        var host = await FarmingSqlServerHost.StartAsync();
        var owner = host.Owner.Client;
        var zoneId = (await owner.CreateZoneAsync()).Id();
        var raftId = (await owner.CreateRaftAsync(zoneId)).Id();
        var cycleId = (await owner.StartCycleAsync(raftId, new CycleBody(Today.AddDays(-10)))).Id();
        await owner.SeedingRecordAsync(cycleId, (await owner.SeedLotAsync()).Session([1, 2, 3, 4, 5]));
        var product = await owner.CreateProductAsync(unitPrice: ListPrice);
        var lotIds = new Guid[lots];
        for (var i = 0; i < lots; i++)
        {
            lotIds[i] = (await owner.HarvestLotAsync(raftId, HarvestFields.OfLanes(i % 5 + 1))).Id();
        }

        var manager = await host.StaffInZoneAsync(zoneId);
        return new World(host, owner, manager.Client, await PurchaseRequestSqlServerTests.TraderOnAsync(host), zoneId, product.Id(), lotIds);
    }

    private static async Task<JsonElement> SoldAsync(World w, params int[] lots)
    {
        var body = Invoice(
            w.Trader.Profile.Id, lots.Select(l => Line(w.ProductId, w.LotIds[l], 10m)).ToArray(), Cash(0m, "PayLater", payLaterOwnerAgreed: true), lots.Select(l => w.LotIds[l]).ToArray());
        return await w.Owner.InvoiceAsync((await w.Owner.SellAsync(body)).GetProperty("invoice").Id());
    }

    private static Task<List<string>> ColumnsAsync(OmtsDbContext db) => db.Database.SqlQueryRaw<string>(
        "SELECT t.name + '.' + c.name AS [Value] FROM sys.columns c JOIN sys.tables t ON t.object_id = c.object_id WHERE t.name <> '__EFMigrationsHistory'").ToListAsync();

    // The migration holds the two tables and the catalogue only; Down drops them with data in them, Up brings them back, and the model has nothing pending.
    [SqlServerFact]
    public async Task Qa_the_migration_adds_only_the_two_tables_and_goes_down_and_up_with_a_return_saved()
    {
        var w = await StartAsync(1);
        await using var _ = w.Host;
        var invoice = await SoldAsync(w, 0);
        await w.Owner.ReturnedAsync(invoice.Id(), ReturnBody(invoice, "BackForSale"));
        await using var db = w.Host.Database.CreateContext();
        Assert.False(db.Database.HasPendingModelChanges());
        var latest = await ColumnsAsync(db);
        var migrator = db.GetService<IMigrator>();

        var chain = db.Database.GetMigrations().ToList();
        var migrationBeforeReturns = chain[chain.IndexOf(ReturnsMigration) - 1];
        await migrator.MigrateAsync(ReturnsMigration);
        var withReturns = await ColumnsAsync(db);
        await migrator.MigrateAsync(migrationBeforeReturns);

        var before = await ColumnsAsync(db);
        Assert.Empty(before.Except(withReturns));
        var added = withReturns.Except(before).ToList();
        Assert.NotEmpty(added);
        Assert.All(added, c => Assert.True(c.StartsWith("SalesReturns.", StringComparison.Ordinal) || c.StartsWith("SalesReturnLines.", StringComparison.Ordinal), c));
        Assert.False(await db.Statuses.AnyAsync(s => s.Group == StatusGroups.SalesReturn));
        Assert.False(await db.StatusTransitions.AnyAsync(t => t.Id == 230));

        await migrator.MigrateAsync();

        Assert.Equal(latest.Order(StringComparer.Ordinal), (await ColumnsAsync(db)).Order(StringComparer.Ordinal));
        Assert.Equal(2, await db.Statuses.CountAsync(s => s.Group == StatusGroups.SalesReturn));
        Assert.Equal(1, await db.Database.SqlQueryRaw<int>(
            "SELECT COUNT(*) AS [Value] FROM sys.indexes WHERE name = 'UX_SalesReturns_Invoice_Recorded' AND is_unique = 1 AND has_filter = 1 AND filter_definition LIKE '%3801%'").SingleAsync());
        Assert.Equal(0, await db.Database.SqlQueryRaw<int>(
            "SELECT COUNT(*) AS [Value] FROM sys.foreign_keys WHERE parent_object_id IN (OBJECT_ID('SalesReturns'), OBJECT_ID('SalesReturnLines')) AND delete_referential_action <> 0").SingleAsync());
        Assert.Empty(await db.Database.GetPendingMigrationsAsync());
    }

    // The provenance filter, the debt sums, the statement, the trace and the yield were only ever run on the in-memory provider by the author's tests, or with an Owner: a query that does not
    // translate answers 500 only here. The Zone Manager records the return, with a close on the way.
    [SqlServerFact]
    public async Task Qa_every_read_of_a_return_translates_on_sql_server_for_a_zone_manager_return_with_a_lot_closed_and_a_lot_reopened()
    {
        var w = await StartAsync();
        await using var _ = w.Host;
        var sold = await w.Owner.SellAsync(Invoice(
            w.Trader.Profile.Id, [Line(w.ProductId, w.LotIds[0], 10m), Line(w.ProductId, w.LotIds[1], 10m)], Cash(600_000m), w.LotIds));
        var invoiceId = sold.GetProperty("invoice").Id();
        var finished = await w.Owner.FinishedAsync(invoiceId, wantsQrCode: true);
        var token = finished.Text("qrUrl")!.Split('/')[^1];
        var anonymous = w.Host.Api.CreateClient();
        var page = await anonymous.ProvenanceAsync(token);
        var codes = page.LotsByCode().Keys.ToList();
        Assert.Equal(2, codes.Count);
        var invoice = await w.Owner.InvoiceAsync(invoiceId);
        var lines = invoice.GetProperty("lines").EnumerateArray().ToList();
        var thrownIndex = lines.FindIndex(l => l.GetProperty("harvestLotId").GetGuid() == w.LotIds[0]);
        var outcomes = thrownIndex == 0 ? new[] { "ThrownAway", "BackForSale" } : new[] { "BackForSale", "ThrownAway" };
        await (await w.Manager.PreviewReturnAsync(invoiceId)).ReadAsync(HttpStatusCode.OK);

        var saved = await w.Manager.ReturnedAsync(invoiceId, ReturnBody(invoice, outcomes).With("ownerAgreed", true).With("thrownAwayLots", new[]
        {
            new { harvestLotId = w.LotIds[0], stillForSale = false, leftoverKg = 5m, closeReason = "Spoiled" },
        }));

        Assert.Equal(-600_000m, saved.Money("debtAfter"));
        var returned = saved.GetProperty("return");
        Assert.Single(await w.Host.QueryAsync(db => db.Notifications.AsNoTracking().Where(n => n.RelatedRecordId == invoiceId && n.Type == "SALES_RETURN_RECORDED" && n.Channel == OMTS.Domain.Enums.NotificationChannel.InApp).ToListAsync()));
        Assert.Single((await anonymous.ProvenanceAsync(token)).LotsByCode().Keys);

        await (await w.Manager.GetSalesReturnAsync(returned.Id())).ReadAsync(HttpStatusCode.OK);
        var after = await w.Owner.InvoiceAsync(invoiceId);
        Assert.Single(after.GetProperty("salesReturns").EnumerateArray());
        await (await w.Owner.GetAsync("/api/sales-invoices?pageSize=20")).ReadAsync(HttpStatusCode.OK);
        var statement = await (await w.Owner.GetAsync($"/api/customer-debts/{w.Trader.Profile.Id}/statement")).ReadAsync(HttpStatusCode.OK);
        Assert.Equal(-600_000m, statement.Money("debtToday"));
        await (await w.Owner.GetAsync("/api/customer-debts?pageSize=20")).ReadAsync(HttpStatusCode.OK);
        var trace = await (await w.Owner.GetAsync($"/api/harvest-lots/{w.LotIds[0]}/trace")).ReadAsync(HttpStatusCode.OK);
        Assert.Equal("ThrownAway", trace.GetProperty("invoices")[0].GetProperty("returns")[0].Text("outcome"));
        var report = await w.Owner.YieldAsync(HarvestYieldTestKit.YieldFilters(Today, Today, zoneIds: w.ZoneId));
        var lost = report.YieldLot(w.LotIds[0]);
        Assert.Equal((10m, 10m, 15m), (lost.Kg("invoicedKg"), lost.Kg("returnedKg"), lost.Kg("lostKg")));
        var preview = await (await w.Owner.GetAsync($"/api/harvest-lots/{w.LotIds[0]}/withdraw-preview")).ReadAsync(HttpStatusCode.OK);
        Assert.Equal(0, preview.GetProperty("traders").GetArrayLength());

        var cancelled = await (await w.Owner.CancelSalesReturnAsync(returned.Id(), CancelBody(returned))).ReadAsync(HttpStatusCode.OK);
        Assert.Equal("Cancelled", cancelled.Text("status"));
        Assert.Equal(2, (await anonymous.ProvenanceAsync(token)).LotsByCode().Count);
        var again = await (await w.Owner.GetAsync($"/api/customer-debts/{w.Trader.Profile.Id}/statement")).ReadAsync(HttpStatusCode.OK);
        Assert.Equal(0m, again.Money("debtToday"));
    }

    [SqlServerFact]
    public async Task Qa_the_largest_vietnamese_reasons_fit_the_columns_and_one_more_character_is_refused()
    {
        var w = await StartAsync(1);
        await using var _ = w.Host;
        var invoice = await SoldAsync(w, 0);
        var reason = string.Concat(Enumerable.Repeat("Hàu chết, thùng số ba. ", 60))[..1000];

        await (await w.Owner.PostReturnAsync(invoice.Id(), ReturnBody(invoice, "BackForSale").With("reason", reason + "x"))).ProblemAsync(HttpStatusCode.BadRequest, "MSG-GEN-01");
        var returned = (await w.Owner.ReturnedAsync(invoice.Id(), ReturnBody(invoice, "BackForSale").With("reason", reason))).GetProperty("return");
        Assert.Equal(reason, returned.Text("reason"));

        await (await w.Owner.CancelSalesReturnAsync(returned.Id(), CancelBody(returned, reason + "x"))).ProblemAsync(HttpStatusCode.BadRequest, "MSG-GEN-01");
        var cancelled = await (await w.Owner.CancelSalesReturnAsync(returned.Id(), CancelBody(returned, reason))).ReadAsync(HttpStatusCode.OK);
        Assert.Equal(reason, cancelled.Text("cancelReason"));
    }

    [SqlServerFact]
    public async Task Qa_a_walk_in_return_by_a_zone_manager_moves_the_invoice_and_its_payment_to_the_profile()
    {
        var w = await StartAsync(1);
        await using var _ = w.Host;
        var sold = await w.Owner.SellAsync(Invoice(null, [Line(w.ProductId, w.LotIds[0], 10m)], Cash(300_000m), w.LotIds[0]));
        var invoice = await w.Owner.InvoiceAsync(sold.GetProperty("invoice").Id());

        var saved = await w.Manager.ReturnedAsync(invoice.Id(), ReturnBody(invoice, "BackForSale").With("ownerAgreed", true).With("customerId", w.Trader.Profile.Id));

        Assert.Equal(-300_000m, saved.Money("debtAfter"));
        Assert.Equal(-300_000m, await w.Owner.CustomerDebtAsync(w.Trader.Profile.Id));
        Assert.All(await w.Host.QueryAsync(db => db.CustomerPayments.AsNoTracking().Where(p => p.InvoiceId == invoice.Id()).ToListAsync()), p => Assert.Equal(w.Trader.Profile.Id, p.CustomerId));
    }

    [SqlServerFact]
    public async Task Qa_two_parallel_cancellations_of_a_return_apply_once_and_never_answer_500()
    {
        var w = await StartAsync(1);
        await using var _ = w.Host;
        for (var round = 0; round < 3; round++)
        {
            var invoice = await SoldAsync(w, 0);
            var returned = (await w.Owner.ReturnedAsync(invoice.Id(), ReturnBody(invoice, "BackForSale"))).GetProperty("return");

            var answers = await InParallelAsync(2, _ => w.Owner.CancelSalesReturnAsync(returned.Id(), CancelBody(returned)));

            Assert.Single(answers, a => a.StatusCode == HttpStatusCode.OK);
            Assert.Contains(await answers.Single(a => a.StatusCode != HttpStatusCode.OK).CodeOrStatusAsync(), new[] { "409 MSG06", "409 MSG-GEN-09" });
            Assert.Equal(1, await w.Host.QueryAsync(db => db.AuditEvents.CountAsync(e => e.RecordId == returned.Id() && e.Action == "SalesReturn.Cancelled")));
        }
    }

    /// <summary>
    /// A sales return recorded between the check of CLAIM-02 and its save: the accept reads the returns, writes neither the invoice nor anything the return writes, so it saves Accepted on a returned
    /// invoice and tells the trader an offer (BR-CLAIM-02, CLAIM-02.0.E2). The seam picks the moment; here the Zone Manager records the return while the Owner accepts.
    /// </summary>
    [SqlServerFact]
    public async Task Qa_a_claim_is_not_accepted_when_a_return_is_recorded_at_the_moment_of_the_accept()
    {
        var w = await StartAsync(1);
        await using var _ = w.Host;
        var invoiceId = (await w.Owner.SellAsync(Invoice(w.Trader.Profile.Id, [Line(w.ProductId, w.LotIds[0], 10m)], Cash(300_000m), w.LotIds[0]))).GetProperty("invoice").Id();
        var claim = await w.Trader.Client.SubmitClaimAsync(invoiceId);
        var rowVersion = (await w.Owner.ClaimAsStaffAsync(claim.Id())).Text("rowVersion");
        var invoice = await w.Owner.InvoiceAsync(invoiceId);
        var racer = new ReturnsTheInvoiceMeanwhile(w.Manager, invoiceId, ReturnBody(invoice, "BackForSale").With("ownerAgreed", true));
        await using var raced = w.Host.Api.WithWebHostBuilder(b => b.ConfigureTestServices(s => s.AddSingleton<IInvoiceSalesReturnCheck>(racer)));
        var owner = raced.CreateClient();
        var tokens = await AcsTestKit.LoginTokensAsync(owner, w.Host.Owner.Account.Username);
        owner.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", tokens.GetProperty("accessToken").GetString());

        var answer = await owner.AcceptClaimAsync(claim.Id(), rowVersion);

        Assert.Equal(1, await w.Host.QueryAsync(db => db.SalesReturns.CountAsync(r => r.InvoiceId == invoiceId && r.StatusId == StatusIds.SalesReturn.Recorded)));
        Assert.NotEqual(HttpStatusCode.OK, answer.StatusCode);
    }

    private sealed class ReturnsTheInvoiceMeanwhile(HttpClient manager, Guid invoiceId, object body) : IInvoiceSalesReturnCheck
    {
        private int _done;

        public async Task<bool> HasSalesReturnAsync(Guid id, CancellationToken cancellationToken)
        {
            if (Interlocked.Exchange(ref _done, 1) == 0)
            {
                await (await manager.PostReturnAsync(invoiceId, body)).ReadAsync(HttpStatusCode.Created);
            }

            return false;
        }
    }
}
