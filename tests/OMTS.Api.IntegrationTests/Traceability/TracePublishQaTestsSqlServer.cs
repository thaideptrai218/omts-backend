using System.Net;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using OMTS.Api.IntegrationTests.Cultivation;
using OMTS.Api.IntegrationTests.Farming;
using OMTS.Api.IntegrationTests.Harvest;
using OMTS.Api.IntegrationTests.Infrastructure;
using OMTS.Domain.Constants;
using OMTS.Domain.Enums;
using static OMTS.Api.IntegrationTests.Farming.FarmingTestKit;
using static OMTS.Api.IntegrationTests.Sales.SalesCatalogueTestKit;
using static OMTS.Api.IntegrationTests.Sales.SalesInvoiceSendingTestKit;
using static OMTS.Api.IntegrationTests.Sales.SalesInvoiceTestKit;

namespace OMTS.Api.IntegrationTests.Traceability;

/// <summary>
/// QA of TRACE-01 on SQL Server with real row versions, the writers of a lot row that the developer's races leave out: two ends of sale of one lot, a sale at the port, and a close by
/// hand (HARVEST-02), each at the same moment as a publish. Never a 500, the lot published once (one audit row), and the sale at the port never lost because of a publish.
/// </summary>
public class TracePublishQaTestsSqlServer(Xunit.Abstractions.ITestOutputHelper output)
{
    private const int Rounds = 3;

    private sealed record Setup(FarmingSqlServerHost Host, HttpClient Owner, HttpClient Manager, Guid RaftId, Guid ProductId, Guid CustomerId);

    private static async Task<Setup> StartAsync()
    {
        var host = await FarmingSqlServerHost.StartAsync();
        var owner = host.Owner.Client;
        var zoneId = (await owner.CreateZoneAsync()).Id();
        var raftId = (await owner.CreateRaftAsync(zoneId)).Id();
        var cycleId = (await owner.StartCycleAsync(raftId, new CycleBody(Today.AddDays(-10)))).Id();
        await owner.SeedingRecordAsync(cycleId, (await owner.SeedLotAsync()).Session([1, 2, 3, 4, 5]));
        var product = await owner.CreateProductAsync(unitPrice: ListPrice);
        var customer = await owner.CreateCustomerAsync();
        var manager = await host.StaffInZoneAsync(zoneId);
        return new Setup(host, owner, manager.Client, raftId, product.Id(), customer.Id());
    }

    /// <summary>A new lot of lane 1 on one invoice of 1 kg that leaves it for sale.</summary>
    private static async Task<(Guid LotId, JsonElement Invoice)> SoldLotAsync(Setup setup)
    {
        var lot = await setup.Owner.HarvestLotAsync(setup.RaftId, HarvestFields.OfLanes(1));
        return (lot.Id(), await SellAsync(setup, lot.Id(), leaveForSale: true));
    }

    private static async Task<JsonElement> SellAsync(Setup setup, Guid lotId, bool leaveForSale) =>
        (await setup.Manager.SellAsync(leaveForSale
            ? Invoice(setup.CustomerId, [Line(setup.ProductId, lotId, 1m)], Cash(ListPrice), lotId)
            : Invoice(setup.CustomerId, [Line(setup.ProductId, lotId, 1m)], Cash(ListPrice)))).GetProperty("invoice");

    private static Task<int> PublishRowsAsync(Setup setup, Guid lotId) => setup.Host.QueryAsync(db => db.AuditEvents.CountAsync(e =>
        e.RecordId == lotId && (e.Action == ProvenancePublishTestKit.PublishedAction || e.Action == ProvenancePublishTestKit.RepublishedAction)));

    private static async Task AssertStatusAsync(int round, string what, HttpResponseMessage answer, HttpStatusCode expected)
    {
        var body = await answer.Content.ReadAsStringAsync();
        Assert.True(answer.StatusCode == expected, $"round {round}: {what} answered {(int)answer.StatusCode} {body}");
    }

    private static Task<OMTS.Domain.Entities.Harvest.HarvestLot> LotAsync(Setup setup, Guid lotId) =>
        setup.Host.QueryAsync(db => db.HarvestLots.AsNoTracking().SingleAsync(l => l.Id == lotId));

    // Two invoices sell the same Not published lot and both end with a Yes at once: one publishes it, the other reads it again as Published; both invoices get their QR code.
    [SqlServerFact]
    public async Task Two_invoices_of_one_lot_ended_at_once_publish_it_once_and_both_get_their_qr_code()
    {
        var setup = await StartAsync();
        await using var _ = setup.Host;
        for (var round = 0; round < Rounds; round++)
        {
            var (lotId, first) = await SoldLotAsync(setup);
            var second = await SellAsync(setup, lotId, leaveForSale: true);

            var answers = await Task.WhenAll(setup.Manager.FinishAsync(first.Id(), true), setup.Owner.FinishAsync(second.Id(), true));

            foreach (var answer in answers)
            {
                var finished = await answer.ReadAsync(HttpStatusCode.OK);
                Assert.NotNull(finished.GetProperty("qrUrl").GetString());
            }

            Assert.Equal(1, await PublishRowsAsync(setup, lotId));
            Assert.Equal(HarvestLotOriginState.Published, (await LotAsync(setup, lotId)).OriginState);
        }
    }

    // SALE-05 at the port while the Owner publishes the lot from the Trace lot screen: the sale (which closes the lot, "sold the whole lot") is saved and the lot is published once.
    [SqlServerFact]
    public async Task A_sale_at_the_port_racing_a_publish_of_its_lot_is_saved_and_the_lot_is_published_once()
    {
        var setup = await StartAsync();
        await using var _ = setup.Host;
        for (var round = 0; round < Rounds; round++)
        {
            var (lotId, _) = await SoldLotAsync(setup);

            var publish = setup.Owner.PostPublishAsync(lotId);
            var sale = setup.Manager.PostInvoiceAsync(Invoice(setup.CustomerId, [Line(setup.ProductId, lotId, 1m)], Cash(ListPrice)));
            await Task.WhenAll(publish, sale);

            await AssertStatusAsync(round, "sale", await sale, HttpStatusCode.Created);
            await AssertStatusAsync(round, "publish", await publish, HttpStatusCode.OK);
            var lot = await LotAsync(setup, lotId);
            Assert.Equal((HarvestLotOriginState.Published, StatusIds.HarvestLot.Closed), (lot.OriginState, lot.StatusId));
            Assert.Equal(1, await PublishRowsAsync(setup, lotId));
        }
    }

    // HARVEST-02 by the Zone Manager while the Owner publishes: the publish retries and saves; the close saves or answers MSG06 (the lot changed under its form); never a 500.
    [SqlServerFact]
    public async Task A_close_by_hand_racing_a_publish_never_answers_500_and_the_lot_is_published_once()
    {
        var setup = await StartAsync();
        await using var _ = setup.Host;
        for (var round = 0; round < Rounds; round++)
        {
            var (lotId, _) = await SoldLotAsync(setup);
            var version = (await setup.Manager.GetHarvestLotAsync(lotId)).GetProperty("rowVersion").GetString();

            var publish = setup.Owner.PostPublishAsync(lotId);
            var close = setup.Manager.PostCloseLotAsync(lotId, 0m, "Unsold", version);
            await Task.WhenAll(publish, close);

            await AssertStatusAsync(round, "publish", await publish, HttpStatusCode.OK);
            var closed = await close;
            var closeBody = await closed.Content.ReadAsStringAsync();
            // Shows whether the two really met: a close that lost answers MSG06.
            output.WriteLine($"round {round}: close {(int)closed.StatusCode}");
            Assert.True(closed.StatusCode is HttpStatusCode.OK or HttpStatusCode.Conflict, $"round {round}: close answered {(int)closed.StatusCode} {closeBody}");
            if (closed.StatusCode == HttpStatusCode.Conflict)
            {
                Assert.Equal("MSG06", await closed.ErrorCodeAsync());
            }

            Assert.Equal(HarvestLotOriginState.Published, (await LotAsync(setup, lotId)).OriginState);
            Assert.Equal(1, await PublishRowsAsync(setup, lotId));
        }
    }
}
