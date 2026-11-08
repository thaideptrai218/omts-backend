using System.Net;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using OMTS.Api.IntegrationTests.Cultivation;
using OMTS.Api.IntegrationTests.Farming;
using OMTS.Api.IntegrationTests.Harvest;
using OMTS.Api.IntegrationTests.Infrastructure;
using OMTS.Domain.Constants;
using OMTS.Domain.Entities.Cultivation;
using OMTS.Domain.Entities.Farming;
using OMTS.Domain.Enums;
using static OMTS.Api.IntegrationTests.Sales.PurchaseRequestTestKit;
using SalesCatalogue = OMTS.Api.IntegrationTests.Sales.SalesCatalogueTestKit;
using static OMTS.Api.IntegrationTests.Sales.SalesInvoiceSendingTestKit;
using static OMTS.Api.IntegrationTests.Sales.SalesInvoiceTestKit;

namespace OMTS.Api.IntegrationTests.Traceability;

/// <summary>
/// QA of TRACE-01 (PR T1a, independent of the developer's tests): what the invoice check promises against what the end of the sale does (SALE-05.9-11), the completeness check
/// (BR-TRACE-01) on lots of a Closed cycle and of another cycle, Closed lots, the end of the sale called twice, a correction after the publish (BR-TRACE-05) and the rights (§I.4.4).
/// </summary>
public class TracePublishQaTests
{
    /// <summary>
    /// DEFECT (Medium) – SALE-05 steps 9-11 and SALE-05.11: the end of the sale happens once, and an invoice that did not get its QR code then never gets one. The invoice check
    /// (TRACE-01.0 steps 1-2) says "a Not published lot with nothing missing is published by the Yes" and <c>getsQrCode</c> "a Yes now gives the QR code", but it never looks at
    /// whether the sale already ended: after a No it still lists the lot as ready and answers <c>getsQrCode: true</c>, while a Yes now publishes nothing and gives no QR code.
    /// </summary>
    [Fact]
    public async Task After_the_end_of_the_sale_the_invoice_check_no_longer_promises_a_publish_or_a_qr_code()
    {
        using var api = new AuthApiFactory();
        var sale = await api.SaleSetupAsync();
        var invoice = await sale.Manager.Client.SoldAsync(sale, sale.LotId);
        await sale.Manager.Client.FinishedAsync(invoice.Id(), wantsQrCode: false);

        var answer = await sale.Manager.Client.GetInvoicePublishCheckAsync(invoice.Id());

        // What a Yes really does now: nothing (the end of the sale was made).
        var yes = await sale.Manager.Client.FinishedAsync(invoice.Id(), wantsQrCode: true);
        Assert.Equal((JsonValueKind.Null, HarvestLotOriginState.NotPublished), (yes.GetProperty("qrUrl").ValueKind, (await api.LotRowAsync(sale.LotId)).OriginState));
        if (answer.StatusCode == HttpStatusCode.OK)
        {
            var check = await answer.ReadAsync(HttpStatusCode.OK);
            Assert.False(check.GetProperty("getsQrCode").GetBoolean(), "The check promises a QR code that the end of the sale, already made, can no longer give.");
        }
        else
        {
            Assert.Equal((HttpStatusCode.Conflict, "MSG-GEN-09"), (answer.StatusCode, await answer.ErrorCodeAsync()));
        }
    }

    // The invoice check and the end of the sale agree on a lot of a zone where the Zone Manager does not publish (BR-SALE-08: the invoice zone is not always the lot's zone).
    [Fact]
    public async Task The_invoice_check_names_a_lot_of_another_zone_as_the_end_of_the_sale_does()
    {
        using var api = new AuthApiFactory();
        var sale = await api.SaleSetupAsync();
        var away = await api.SeededRaftAsync();
        var awayLot = (await away.Owner.Client.HarvestLotAsync(away.RaftId, HarvestFields.OfLanes(1))).Id();
        var body = Invoice(sale.CustomerId, [Line(sale.ProductId, sale.LotId, 1m), Line(sale.ProductId, awayLot, 1m)], Cash(60_000m), sale.LotId, awayLot).With("zoneId", sale.ZoneId);
        var invoice = (await sale.Owner.Client.SellAsync(body)).GetProperty("invoice");

        var check = (await (await sale.Manager.Client.GetInvoicePublishCheckAsync(invoice.Id())).ReadAsync(HttpStatusCode.OK)).LotsById();
        var finished = (await sale.Manager.Client.FinishedAsync(invoice.Id(), wantsQrCode: true)).LotsById();

        Assert.Equal(check[awayLot].Missing(), finished[awayLot].Missing());
        Assert.Equal(check[awayLot].Text("message"), finished[awayLot].Text("message"));
        Assert.Equal((0, "Published"), (check[sale.LotId].GetProperty("missing").GetArrayLength(), finished[sale.LotId].Text("originState")));
    }

    // BR-TRACE-01 holds for a lot of a Closed cycle (TRACE-02 / TRACE-03 cycles are Open or Closed), also after its lane was removed (BR-FARM-04: a removed lane keeps its records).
    [Fact]
    public async Task A_sold_lot_of_a_closed_cycle_whose_lane_was_removed_after_the_close_is_published()
    {
        using var api = new AuthApiFactory();
        var raft = await api.SeededRaftAsync();
        var lot = (await raft.Owner.Client.HarvestLotAsync(raft.RaftId, HarvestFields.OfLanes(1))).Id();
        var product = (await SalesCatalogue.CreateProductAsync(raft.Owner.Client, unitPrice: ListPrice)).Id();
        var customer = (await SalesCatalogue.CreateCustomerAsync(raft.Owner.Client)).Id();
        var manager = await api.ManagerAsync(raft.ZoneId);
        await manager.Client.SellAsync(Invoice(customer, [Line(product, lot, 1m)], Cash(ListPrice), lot));
        await api.CloseCycleAsync(raft.CycleId);
        await api.UpdateAsync<Lane>(await api.QueryAsync(db => db.Lanes.Where(l => l.RaftId == raft.RaftId && l.LaneNumber == 1).Select(l => l.Id).SingleAsync()),
            l => l.StatusId = StatusIds.Lane.Removed);

        var check = await manager.Client.PublishCheckAsync(lot);
        Assert.Equal((true, true), (check.GetProperty("canPublish").GetBoolean(), check.GetProperty("ready").GetBoolean()));

        var published = await (await manager.Client.PostPublishAsync(lot)).ReadAsync(HttpStatusCode.OK);
        Assert.Equal("Published", published.GetProperty("lot").Text("originState"));
    }

    // BR-TRACE-01: the seeding session must be in the lot's cycle; a session of another cycle on another raft does not count.
    [Fact]
    public async Task A_seeding_session_moved_to_another_cycle_no_longer_gives_the_lot_its_seed_lot()
    {
        using var api = new AuthApiFactory();
        var sale = await api.SaleSetupAsync();
        await sale.Manager.Client.SoldAsync(sale, sale.LotId);
        var away = await api.CycleAsync();
        var session = await api.QueryAsync(db => db.SeedingRecords.Where(s => s.Cycle.RaftId == sale.RaftId).Select(s => s.Id).SingleAsync());
        await api.UpdateAsync<SeedingRecord>(session, s => s.CycleId = away.CycleId);

        var refused = await (await sale.Manager.Client.PostPublishAsync(sale.LotId)).ReadAsync(HttpStatusCode.Conflict);

        Assert.Equal("MSG-TRACE-01", refused.Text("code"));
        Assert.Equal(["NoSeedingSession 1"], refused.Missing());
        Assert.Empty(await api.PublishAuditAsync(sale.LotId));
    }

    // H2 / BR-HARVEST-04: a lot closed by its sale ("sold the whole lot") is still published, at the end of the sale or from the Trace lot screen.
    [Fact]
    public async Task A_lot_closed_by_its_sale_is_published_at_the_end_of_the_sale_and_from_the_lot_screen()
    {
        using var api = new AuthApiFactory();
        var sale = await api.SaleSetupAsync();
        var first = (await sale.Manager.Client.SellAsync(Invoice(sale.CustomerId, [Line(sale.ProductId, sale.LotId, 10m)], Cash(300_000m)))).GetProperty("invoice");
        var second = (await sale.Manager.Client.SellAsync(Invoice(sale.CustomerId, [Line(sale.ProductId, sale.OtherLotId, 10m)], Cash(300_000m)))).GetProperty("invoice");
        foreach (var id in new[] { sale.LotId, sale.OtherLotId })
        {
            Assert.Equal(StatusIds.HarvestLot.Closed, (await api.LotRowAsync(id)).StatusId);
        }

        var finished = await sale.Manager.Client.FinishedAsync(first.Id(), wantsQrCode: true);
        await sale.Manager.Client.FinishedAsync(second.Id(), wantsQrCode: false);
        var published = await (await sale.Manager.Client.PostPublishAsync(sale.OtherLotId)).ReadAsync(HttpStatusCode.OK);

        Assert.Equal(("Published", "Published"), (finished.LotsById()[sale.LotId].Text("originState"), published.GetProperty("lot").Text("originState")));
        Assert.NotNull(finished.Text("qrUrl"));
    }

    // SALE-05: the end of the sale happens once. A lot that became ready after a Yes that published nothing is not published by a second call, and nothing is sent again.
    [Fact]
    public async Task A_second_end_of_the_sale_publishes_nothing_even_when_a_lot_became_ready_since()
    {
        using var api = new AuthApiFactory();
        var sale = await api.SaleSetupAsync();
        await api.CancelSeedReceiptAsync(sale.RaftId);
        var invoice = await sale.Manager.Client.SoldAsync(sale, sale.LotId);
        var first = await sale.Manager.Client.FinishedAsync(invoice.Id(), wantsQrCode: true);
        Assert.Equal((JsonValueKind.Null, "MSG-TRACE-01"), (first.GetProperty("qrUrl").ValueKind, first.LotsById()[sale.LotId].Text("messageCode")));
        await api.DbAsync(async db =>
        {
            var receipt = await db.SeedingRecords.Where(s => s.Cycle.RaftId == sale.RaftId).Select(s => s.SeedLotLine.Receipt).SingleAsync();
            receipt.StatusId = StatusIds.PurchaseReceipt.Completed;
            return await db.SaveChangesAsync();
        });

        var second = await sale.Manager.Client.FinishedAsync(invoice.Id(), wantsQrCode: true);

        Assert.Equal((JsonValueKind.Null, 0), (second.GetProperty("qrUrl").ValueKind, second.GetProperty("lots").GetArrayLength()));
        Assert.Equal(HarvestLotOriginState.NotPublished, (await api.LotRowAsync(sale.LotId)).OriginState);
        Assert.Empty(await api.PublishAuditAsync(sale.LotId));
        Assert.Single(await api.InvoiceSendingsAsync(invoice.Id()));
    }

    // BR-TRACE-05: a TRACE-03 correction of the seed lot of a lane after the publish shows at once and does not change whether the lot is Published.
    [Fact]
    public async Task A_seed_lot_corrected_after_the_publish_leaves_the_lot_published_and_ready()
    {
        using var api = new AuthApiFactory();
        var raft = await api.SeededRaftAsync();
        var lot = (await raft.Owner.Client.HarvestLotAsync(raft.RaftId, HarvestFields.OfLanes(1))).Id();
        var product = (await SalesCatalogue.CreateProductAsync(raft.Owner.Client, unitPrice: ListPrice)).Id();
        var customer = (await SalesCatalogue.CreateCustomerAsync(raft.Owner.Client)).Id();
        var manager = await api.ManagerAsync(raft.ZoneId);
        var invoice = (await manager.Client.SellAsync(Invoice(customer, [Line(product, lot, 1m)], Cash(ListPrice), lot))).GetProperty("invoice");
        await manager.Client.FinishedAsync(invoice.Id(), wantsQrCode: true);
        var publishedAt = (await api.LotRowAsync(lot)).PublishedAt;
        Assert.NotNull(publishedAt);
        var other = await raft.Owner.Client.SeedLotAsync();

        await (await raft.Owner.Client.PutSeedingTraceAsync(raft.Session.Id(), raft.Session.SeedingCorrection(seedLotId: other.LineId))).ReadAsync(HttpStatusCode.OK);

        var row = await api.LotRowAsync(lot);
        Assert.Equal((HarvestLotOriginState.Published, publishedAt), (row.OriginState, row.PublishedAt));
        Assert.Empty((await manager.Client.GetHarvestLotAsync(lot)).Missing("originMissing"));
        Assert.Single(await api.PublishAuditAsync(lot));
    }

    // TRACE-01 PRE-2: a Withdrawn lot is published again by the Owner whatever its invoices; here its only invoice is cancelled.
    [Fact]
    public async Task The_owner_publishes_again_a_withdrawn_lot_whose_only_invoice_is_cancelled()
    {
        using var api = new AuthApiFactory();
        var sale = await api.SaleSetupAsync();
        var invoice = await sale.Manager.Client.SoldAsync(sale, sale.LotId);
        await api.CancelInvoiceAsync(invoice.Id());
        await api.WithdrawAsync(sale.LotId);

        var republished = await (await sale.Owner.Client.PostPublishAsync(sale.LotId)).ReadAsync(HttpStatusCode.OK);

        Assert.Equal(("Published", true), (republished.GetProperty("lot").Text("originState"), republished.GetProperty("republished").GetBoolean()));
        Assert.Equal(ProvenancePublishTestKit.RepublishedAction, Assert.Single(await api.PublishAuditAsync(sale.LotId)).Action);
    }

    // §I.4.4: a trader account (no staff right) and an anonymous caller reach none of the three routes; a Zone Manager of All zones publishes in any zone.
    [Fact]
    public async Task A_trader_and_an_anonymous_caller_are_refused_and_an_all_zones_zone_manager_publishes()
    {
        using var api = new AuthApiFactory();
        var sale = await api.SaleSetupAsync();
        var invoice = await sale.Manager.Client.SoldAsync(sale, sale.LotId);
        var trader = await api.TraderAsync();
        var anonymous = api.CreateClient();

        foreach (var (client, expected) in new[] { (trader, HttpStatusCode.Forbidden), (anonymous, HttpStatusCode.Unauthorized) })
        {
            foreach (var answer in new[] { await client.GetPublishCheckAsync(sale.LotId), await client.PostPublishAsync(sale.LotId), await client.GetInvoicePublishCheckAsync(invoice.Id()) })
            {
                Assert.Equal(expected, answer.StatusCode);
            }
        }

        Assert.Empty(await api.PublishAuditAsync(sale.LotId));
        var allZones = await api.StaffAsync(null);
        Assert.True((await allZones.Client.PublishCheckAsync(sale.LotId)).GetProperty("canPublish").GetBoolean());
        await (await allZones.Client.PostPublishAsync(sale.LotId)).ReadAsync(HttpStatusCode.OK);
        Assert.Equal(allZones.Account.Id, Assert.Single(await api.PublishAuditAsync(sale.LotId)).ActorAccountId);
    }
}
