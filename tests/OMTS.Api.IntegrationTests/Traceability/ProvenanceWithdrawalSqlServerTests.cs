using System.Net;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using OMTS.Api.IntegrationTests.Cultivation;
using OMTS.Api.IntegrationTests.Farming;
using OMTS.Api.IntegrationTests.Harvest;
using OMTS.Api.IntegrationTests.Infrastructure;
using OMTS.Application.Features.Notifications;
using OMTS.Domain.Entities.Harvest;
using OMTS.Domain.Enums;
using static OMTS.Api.IntegrationTests.Farming.FarmingTestKit;
using static OMTS.Api.IntegrationTests.Sales.SalesCatalogueTestKit;
using static OMTS.Api.IntegrationTests.Sales.SalesInvoiceSendingTestKit;
using static OMTS.Api.IntegrationTests.Sales.SalesInvoiceTestKit;

namespace OMTS.Api.IntegrationTests.Traceability;

/// <summary>
/// TRACE-04 on SQL Server with real row versions: two withdrawals of one lot, a withdrawal while the Owner publishes it again, and a withdrawal while the end of a sale of the same lot
/// decides its QR code. Every answer is a 200 or a 409, never a 500; the lot is withdrawn once per winner and each trader is told once.
/// (The end of a sale does not write a lot that is already Published: parallel ends of the sales of one lot never conflict, see ProvenanceWithdrawalQaSqlServerTests.)
/// </summary>
public class ProvenanceWithdrawalSqlServerTests
{
    private const int Rounds = 3;

    private sealed record Setup(FarmingSqlServerHost Host, HttpClient Owner, HttpClient Manager, Guid RaftId, Guid ProductId, Guid CustomerId, Guid OtherCustomerId);

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
        var customer = await owner.CreateCustomerAsync(name: "DEMO trader one");
        var other = await owner.CreateCustomerAsync(name: "DEMO trader two");
        var manager = await host.StaffInZoneAsync(zoneId);
        return new Setup(host, owner, manager.Client, raftId, product.Id(), customer.Id(), other.Id());
    }

    private static async Task<JsonElement> SellAsync(Setup setup, Guid lotId, Guid customerId) =>
        (await setup.Manager.SellAsync(Invoice(customerId, [Line(setup.ProductId, lotId, 1m)], Cash(ListPrice), lotId))).GetProperty("invoice");

    /// <summary>A new lot of lane 1, sold to the first trader and published from the Trace lot screen.</summary>
    private static async Task<Guid> PublishedLotAsync(Setup setup)
    {
        var lotId = (await setup.Owner.HarvestLotAsync(setup.RaftId, HarvestFields.OfLanes(1))).Id();
        await SellAsync(setup, lotId, setup.CustomerId);
        await (await setup.Owner.PostPublishAsync(lotId)).ReadAsync(HttpStatusCode.OK);
        return lotId;
    }

    private static Task<HarvestLot> LotAsync(Setup setup, Guid lotId) => setup.Host.QueryAsync(db => db.HarvestLots.AsNoTracking().SingleAsync(l => l.Id == lotId));

    private static Task<int> AuditAsync(Setup setup, Guid lotId, string action) =>
        setup.Host.QueryAsync(db => db.AuditEvents.CountAsync(e => e.RecordId == lotId && e.Action == action));

    private static Task<int> SendingsAsync(Setup setup, Guid lotId) => setup.Host.QueryAsync(db => db.Notifications.CountAsync(n =>
        n.RelatedRecordId == lotId && n.Type == NotificationTypes.ProvenanceWithdrawn && n.Channel != NotificationChannel.InApp));

    [SqlServerFact]
    public async Task Two_parallel_withdrawals_withdraw_the_lot_once_and_tell_the_trader_once()
    {
        var setup = await StartAsync();
        await using var _ = setup.Host;
        for (var round = 0; round < Rounds; round++)
        {
            var lotId = await PublishedLotAsync(setup);

            var answers = await Task.WhenAll(setup.Owner.PostWithdrawAsync(lotId, "First"), setup.Owner.PostWithdrawAsync(lotId, "Second"));

            Assert.Equal([HttpStatusCode.OK, HttpStatusCode.Conflict], answers.Select(a => a.StatusCode).Order());
            Assert.Equal("MSG-GEN-09", await answers.Single(a => a.StatusCode == HttpStatusCode.Conflict).ErrorCodeAsync());
            Assert.Equal((1, 1), (await AuditAsync(setup, lotId, ProvenanceWithdrawalTestKit.WithdrawnAction), await SendingsAsync(setup, lotId)));
            Assert.Equal(HarvestLotOriginState.Withdrawn, (await LotAsync(setup, lotId)).OriginState);
        }
    }

    // The Owner withdraws and publishes again at the same moment: the publish of a Published lot is refused, unless the withdrawal saved before it read the lot.
    [SqlServerFact]
    public async Task A_withdrawal_and_a_publish_again_at_the_same_moment_leave_a_state_the_audit_log_explains()
    {
        var setup = await StartAsync();
        await using var _ = setup.Host;
        for (var round = 0; round < Rounds; round++)
        {
            var lotId = await PublishedLotAsync(setup);

            var answers = await Task.WhenAll(setup.Owner.PostWithdrawAsync(lotId), setup.Owner.PostPublishAsync(lotId));

            Assert.Equal(HttpStatusCode.OK, answers[0].StatusCode);
            var publish = answers[1];
            Assert.True(publish.StatusCode is HttpStatusCode.OK or HttpStatusCode.Conflict, $"Publish answered {publish.StatusCode}.");
            var republished = publish.StatusCode == HttpStatusCode.OK;
            Assert.Equal(republished ? HarvestLotOriginState.Published : HarvestLotOriginState.Withdrawn, (await LotAsync(setup, lotId)).OriginState);
            Assert.Equal((1, republished ? 1 : 0), (await AuditAsync(setup, lotId, ProvenanceWithdrawalTestKit.WithdrawnAction),
                await AuditAsync(setup, lotId, ProvenancePublishTestKit.RepublishedAction)));
            Assert.Equal(1, await SendingsAsync(setup, lotId));
        }
    }

    // BR-TRACE-02 read as serial order: the end of a sale reads the lot and writes the invoice, the withdrawal reads the invoices and writes the lot, so the two never conflict. A QR
    // code decided just before the withdrawal saved is the order "end of the sale, then withdrawal"; whichever order the database took, nothing fails, the lot ends Withdrawn and the
    // trader of that invoice, saved before both, is told by the withdrawal.
    [SqlServerFact]
    public async Task A_withdrawal_and_the_end_of_a_sale_of_the_same_lot_both_succeed_and_a_trader_with_a_qr_code_is_told()
    {
        var setup = await StartAsync();
        await using var _ = setup.Host;
        for (var round = 0; round < Rounds; round++)
        {
            var lotId = await PublishedLotAsync(setup);
            var invoice = await SellAsync(setup, lotId, setup.OtherCustomerId);

            var answers = await Task.WhenAll(setup.Owner.PostWithdrawAsync(lotId), setup.Manager.FinishAsync(invoice.Id(), true));

            var withdrawn = await answers[0].ReadAsync(HttpStatusCode.OK);
            await answers[1].ReadAsync(HttpStatusCode.OK);
            Assert.Equal(HarvestLotOriginState.Withdrawn, (await LotAsync(setup, lotId)).OriginState);
            var told = withdrawn.GetProperty("notices").EnumerateArray().Select(n => n.GetProperty("customerId").GetGuid()).ToList();
            Assert.Equal(new[] { setup.CustomerId, setup.OtherCustomerId }.Order(), told.Order());
            var saved = await setup.Host.QueryAsync(db => db.SalesInvoices.AsNoTracking().SingleAsync(i => i.Id == invoice.Id()));
            Assert.True(saved.QrToken is null || told.Contains(setup.OtherCustomerId), "A QR code was made for an invoice whose trader the withdrawal did not tell.");
            Assert.Equal(1, await AuditAsync(setup, lotId, ProvenancePublishTestKit.PublishedAction));
        }
    }
}
