using System.Net;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using OMTS.Api.IntegrationTests.Cultivation;
using OMTS.Api.IntegrationTests.Farming;
using OMTS.Api.IntegrationTests.Harvest;
using OMTS.Api.IntegrationTests.Infrastructure;
using OMTS.Domain.Enums;
using static OMTS.Api.IntegrationTests.Farming.FarmingTestKit;
using static OMTS.Api.IntegrationTests.Sales.SalesCatalogueTestKit;
using static OMTS.Api.IntegrationTests.Sales.SalesInvoiceSendingTestKit;
using static OMTS.Api.IntegrationTests.Sales.SalesInvoiceTestKit;

namespace OMTS.Api.IntegrationTests.Traceability;

/// <summary>
/// TRACE-01 on SQL Server with real row versions: two publishes of one lot, and a publish from the Trace lot screen while the end of a sale publishes the same lot. The lot is
/// published once (one publish time, one audit row) and every answer is a 200 or a 409, never a 500.
/// </summary>
public class ProvenancePublishSqlServerTests
{
    private const int Rounds = 5;

    private sealed record Setup(FarmingSqlServerHost Host, HttpClient Owner, HttpClient Manager, Guid RaftId, Guid ProductId, Guid CustomerId);

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
        var manager = await host.StaffInZoneAsync(zoneId);
        return new Setup(host, owner, manager.Client, raftId, product.Id(), customer.Id());
    }

    /// <summary>A new lot of lane 1, sold by the Zone Manager on an invoice of 1 kg.</summary>
    private static async Task<(Guid LotId, JsonElement Invoice)> SoldLotAsync(Setup setup)
    {
        var lot = await setup.Owner.HarvestLotAsync(setup.RaftId, HarvestFields.OfLanes(1));
        var invoice = (await setup.Manager.SellAsync(Invoice(setup.CustomerId, [Line(setup.ProductId, lot.Id(), 1m)], Cash(ListPrice), lot.Id()))).GetProperty("invoice");
        return (lot.Id(), invoice);
    }

    private static Task<int> PublishRowsAsync(Setup setup, Guid lotId) => setup.Host.QueryAsync(db => db.AuditEvents.CountAsync(e =>
        e.RecordId == lotId && (e.Action == ProvenancePublishTestKit.PublishedAction || e.Action == ProvenancePublishTestKit.RepublishedAction)));

    [SqlServerFact]
    public async Task Two_parallel_publishes_of_one_lot_publish_it_once()
    {
        var setup = await StartAsync();
        await using var _ = setup.Host;
        for (var round = 0; round < Rounds; round++)
        {
            var (lotId, _) = await SoldLotAsync(setup);

            var answers = await Task.WhenAll(setup.Manager.PostPublishAsync(lotId), setup.Owner.PostPublishAsync(lotId));

            Assert.Equal([HttpStatusCode.OK, HttpStatusCode.Conflict], answers.Select(a => a.StatusCode).Order());
            Assert.Equal("MSG-GEN-09", await answers.Single(a => a.StatusCode == HttpStatusCode.Conflict).ErrorCodeAsync());
            Assert.Equal(1, await PublishRowsAsync(setup, lotId));
        }
    }

    // A Zone Manager publishes from the Trace lot screen while another ends the sale with a Yes: whichever saves first publishes the lot, the end of the sale reads it again and
    // gives the QR code, the other publish answers MSG-GEN-09.
    [SqlServerFact]
    public async Task A_publish_from_the_lot_screen_and_the_end_of_a_sale_publish_the_lot_once_and_the_invoice_gets_its_qr_code()
    {
        var setup = await StartAsync();
        await using var _ = setup.Host;
        for (var round = 0; round < Rounds; round++)
        {
            var (lotId, invoice) = await SoldLotAsync(setup);

            var answers = await Task.WhenAll(setup.Owner.PostPublishAsync(lotId), setup.Manager.FinishAsync(invoice.Id(), true));

            var publish = answers[0];
            Assert.True(publish.StatusCode is HttpStatusCode.OK or HttpStatusCode.Conflict, $"Publish answered {publish.StatusCode}.");
            if (publish.StatusCode == HttpStatusCode.Conflict)
            {
                Assert.Equal("MSG-GEN-09", await publish.ErrorCodeAsync());
            }

            var finished = await answers[1].ReadAsync(HttpStatusCode.OK);
            Assert.NotNull(finished.GetProperty("qrUrl").GetString());
            Assert.Equal(1, await PublishRowsAsync(setup, lotId));
            Assert.Equal(HarvestLotOriginState.Published, (await setup.Host.QueryAsync(db => db.HarvestLots.AsNoTracking().SingleAsync(l => l.Id == lotId))).OriginState);
        }
    }
}
