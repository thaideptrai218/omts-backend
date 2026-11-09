using System.Net;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using OMTS.Api.IntegrationTests.Cultivation;
using OMTS.Api.IntegrationTests.Farming;
using OMTS.Api.IntegrationTests.Harvest;
using OMTS.Api.IntegrationTests.Infrastructure;
using OMTS.Application.Features.Sales.Invoices;
using OMTS.Domain.Constants;
using OMTS.Domain.Entities.Sales;
using OMTS.Domain.Enums;
using static OMTS.Api.IntegrationTests.Farming.FarmingTestKit;
using static OMTS.Api.IntegrationTests.Sales.SalesInvoiceChangeTestKit;
using static OMTS.Api.IntegrationTests.Sales.SalesInvoiceTestKit;

namespace OMTS.Api.IntegrationTests.Sales;

/// <summary>SALE-07 and SALE-08 with real row versions: of two parallel changes of one version, one wins and the other is told, so nothing is applied twice; the lines keep their unique numbers.</summary>
public class SalesInvoiceChangeSqlServerTests
{
    private const int Rounds = 3;

    private sealed record Setup(FarmingSqlServerHost Host, HttpClient Owner, Guid RaftId, Guid ProductId, Guid CustomerId);

    private static async Task<Setup> StartAsync()
    {
        var host = await FarmingSqlServerHost.StartAsync();
        var owner = host.Owner.Client;
        var zoneId = (await owner.CreateZoneAsync()).Id();
        var raftId = (await owner.CreateRaftAsync(zoneId)).Id();
        var cycleId = (await owner.StartCycleAsync(raftId, new CycleBody(Today.AddDays(-10)))).Id();
        var seed = await owner.SeedLotAsync();
        await owner.SeedingRecordAsync(cycleId, seed.Session([1, 2, 3, 4, 5]));
        var product = await owner.CreateProductAsync(unitPrice: ListPrice);
        var customer = await owner.CreateCustomerAsync();
        return new Setup(host, owner, raftId, product.Id(), customer.Id());
    }

    private static async Task<Guid> LotAsync(Setup setup) => (await setup.Owner.HarvestLotAsync(setup.RaftId, HarvestFields.OfLanes(1))).Id();

    /// <summary>The sale with the mark of the lot set, closing it; on the debt, finished so the correction sends.</summary>
    private static async Task<JsonElement> SoldAsync(Setup setup, Guid lotId, decimal kg = 10m)
    {
        var sold = (await setup.Owner.SellAsync(Invoice(setup.CustomerId, [Line(setup.ProductId, lotId, kg, 50_000m)], Cash(0m, "PayLater", payLaterOwnerAgreed: true)))).GetProperty("invoice");
        await setup.Owner.FinishedAsync(sold.Id(), wantsQrCode: false);
        return await setup.Owner.InvoiceAsync(sold.Id());
    }

    [SqlServerFact]
    public async Task Two_parallel_price_cuts_of_one_version_apply_once()
    {
        var setup = await StartAsync();
        await using var _ = setup.Host;
        for (var round = 0; round < Rounds; round++)
        {
            var debtBefore = await setup.Owner.CustomerDebtAsync(setup.CustomerId);
            var invoice = await SoldAsync(setup, await LotAsync(setup));

            var answers = await Task.WhenAll(setup.Owner.CorrectAsync(invoice.Id(), PriceCut(invoice, 100_000m)), setup.Owner.CorrectAsync(invoice.Id(), PriceCut(invoice, 100_000m)));

            Assert.Single(answers, a => a.StatusCode == HttpStatusCode.OK);
            Assert.Single(answers, a => a.StatusCode == HttpStatusCode.Conflict);
            var saved = await setup.Owner.InvoiceAsync(invoice.Id());
            Assert.Equal((400_000m, 100_000m), (saved.Money("total"), saved.Money("priceCutTotal")));
            Assert.Equal(debtBefore + 400_000m, await setup.Owner.CustomerDebtAsync(setup.CustomerId));
            Assert.Equal(1, await setup.Host.QueryAsync(db => db.Set<SalesInvoicePriceCut>().CountAsync(c => c.InvoiceId == invoice.Id())));
            Assert.Equal(1, await setup.Host.QueryAsync(db => db.AuditEvents.CountAsync(e => e.RecordId == invoice.Id() && e.Action == SalesInvoiceCorrectionWriter.CorrectedAction)));
        }
    }

    [SqlServerFact]
    public async Task Two_parallel_cancellations_apply_once_and_reopen_the_lot_once()
    {
        var setup = await StartAsync();
        await using var _ = setup.Host;
        for (var round = 0; round < Rounds; round++)
        {
            var debtBefore = await setup.Owner.CustomerDebtAsync(setup.CustomerId);
            var lotId = await LotAsync(setup);
            var invoice = await SoldAsync(setup, lotId);

            var answers = await Task.WhenAll(setup.Owner.CancelInvoiceAsync(invoice.Id(), Cancellation(invoice)), setup.Owner.CancelInvoiceAsync(invoice.Id(), Cancellation(invoice)));

            Assert.Single(answers, a => a.StatusCode == HttpStatusCode.OK);
            Assert.Single(answers, a => a.StatusCode == HttpStatusCode.Conflict);
            Assert.Equal(debtBefore, await setup.Owner.CustomerDebtAsync(setup.CustomerId));
            Assert.Equal(1, await setup.Host.QueryAsync(db => db.AuditEvents.CountAsync(e => e.RecordId == invoice.Id() && e.Action == SalesInvoiceCancellationService.CancelledAction)));
            Assert.Equal(1, await setup.Host.QueryAsync(db => db.AuditEvents.CountAsync(e => e.RecordId == lotId && e.Action == "HarvestLot.Reopened")));
            Assert.Equal(1, await setup.Host.QueryAsync(db => db.Notifications.CountAsync(n => n.RelatedRecordId == invoice.Id() && n.Type == SalesInvoiceMessages.CancellationType)));
        }
    }

    // A correction and a cancellation at once: one wins whatever the order, and the other changes nothing.
    [SqlServerFact]
    public async Task A_correction_and_a_parallel_cancellation_cannot_both_win()
    {
        var setup = await StartAsync();
        await using var _ = setup.Host;
        for (var round = 0; round < Rounds; round++)
        {
            var invoice = await SoldAsync(setup, await LotAsync(setup));

            var answers = await Task.WhenAll(setup.Owner.CorrectAsync(invoice.Id(), PriceCut(invoice, 100_000m)), setup.Owner.CancelInvoiceAsync(invoice.Id(), Cancellation(invoice)));

            Assert.Single(answers, a => a.StatusCode == HttpStatusCode.OK);
            Assert.Single(answers, a => a.StatusCode == HttpStatusCode.Conflict);
            var saved = await setup.Owner.InvoiceAsync(invoice.Id());
            Assert.True((saved.Text("status") == "Cancelled") ^ (saved.Money("priceCutTotal") == 100_000m));
        }
    }

    [SqlServerFact]
    public async Task A_stale_version_is_refused_and_a_correction_shrinks_and_grows_the_lines_on_their_unique_numbers()
    {
        var setup = await StartAsync();
        await using var _ = setup.Host;
        var lotA = await LotAsync(setup);
        var lotB = await LotAsync(setup);
        var invoice = await SoldAsync(setup, lotA);
        var current = (await setup.Owner.CorrectedAsync(invoice.Id(), DataEntry(invoice, [Line(setup.ProductId, lotA, 10m, 50_000m), Line(setup.ProductId, lotB, 2m, 50_000m)], lotA, lotB))).GetProperty("invoice");

        var stale = await (await setup.Owner.CorrectAsync(invoice.Id(), PriceCut(invoice, 1_000m))).ReadAsync(HttpStatusCode.Conflict);
        Assert.Equal("MSG06", stale.Text("code"));

        var shrunk = (await setup.Owner.CorrectedAsync(invoice.Id(), DataEntry(current, [Line(setup.ProductId, lotB, 3m, 50_000m)], lotB))).GetProperty("invoice");
        var grown = (await setup.Owner.CorrectedAsync(invoice.Id(), DataEntry(shrunk, [Line(setup.ProductId, lotA, 1m, 50_000m), Line(setup.ProductId, lotB, 1m, 50_000m)], lotA, lotB))).GetProperty("invoice");

        Assert.Equal([1, 2], grown.GetProperty("lines").EnumerateArray().Select(l => l.GetProperty("lineNumber").GetInt32()).ToList());
        Assert.Equal(100_000m, grown.Money("total"));
    }

    [SqlServerFact]
    public async Task The_migration_adds_nullable_columns_and_goes_down_and_up_again_with_no_pending_change()
    {
        await using var database = SqlServerTestDatabase.Create();
        await using var db = database.CreateContext();
        var migrator = db.GetService<IMigrator>();
        await migrator.MigrateAsync();
        Assert.False(db.Database.HasPendingModelChanges());

        await migrator.MigrateAsync("20261108020405_CustomerPayments");

        Assert.Equal(0, await db.Database.SqlQueryRaw<int>(
            "SELECT COUNT(*) AS [Value] FROM sys.columns WHERE object_id = OBJECT_ID('SalesInvoices') AND name IN ('CancelledAt', 'CancelReason', 'LastCorrectionKind')").SingleAsync());

        await migrator.MigrateAsync();

        Assert.Equal(3, await db.Database.SqlQueryRaw<int>(
            "SELECT COUNT(*) AS [Value] FROM sys.columns WHERE object_id = OBJECT_ID('SalesInvoices') AND name IN ('CancelledAt', 'CancelReason', 'LastCorrectionKind') AND is_nullable = 1").SingleAsync());
        Assert.Empty(await db.Database.GetPendingMigrationsAsync());
    }
}
