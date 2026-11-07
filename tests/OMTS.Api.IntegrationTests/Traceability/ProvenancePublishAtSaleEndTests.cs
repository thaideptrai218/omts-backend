using System.Net;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using OMTS.Api.IntegrationTests.Farming;
using OMTS.Api.IntegrationTests.Harvest;
using OMTS.Api.IntegrationTests.Infrastructure;
using OMTS.Domain.Enums;
using static OMTS.Api.IntegrationTests.Sales.SalesInvoiceSendingTestKit;
using static OMTS.Api.IntegrationTests.Sales.SalesInvoiceTestKit;

namespace OMTS.Api.IntegrationTests.Traceability;

/// <summary>TRACE-01 at the end of a sale (SALE-05.9, SALE-05.11): the check before the Yes, the ready lots published in the save of the end of the sale, AC-12.</summary>
public class ProvenancePublishAtSaleEndTests
{
    private static string NotReady(string lotCode, string missing) =>
        $"Harvest lot {lotCode} cannot be published yet: {missing}. The invoice is saved; the other lots are not affected.";

    // AC-12: of the two lots, the one with all its origin information is published by the Yes and the other stays Not published (MSG-TRACE-01); the invoice gets one QR code and is
    // sent with its link. The check before the Yes changes nothing.
    [Fact]
    public async Task Ac_12_the_end_of_the_sale_publishes_the_ready_lot_and_the_invoice_gets_one_qr_code()
    {
        using var api = new AuthApiFactory();
        var sale = await api.SaleSetupAsync();
        await api.LeaveLaneWithoutSeedLotAsync(sale.RaftId, 2);
        var invoice = (await sale.Manager.Client.SellAsync(Invoice(
                sale.CustomerId, [Line(sale.ProductId, sale.LotId, 10m), Line(sale.ProductId, sale.OtherLotId, 5m)], Cash(450_000m), sale.LotId, sale.OtherLotId)))
            .GetProperty("invoice");
        var code = (await api.LotRowAsync(sale.LotId)).LotCode;
        var otherCode = (await api.LotRowAsync(sale.OtherLotId)).LotCode;
        const string missing = "lane 2 has no seeding session in the cycle of the lot";

        // TRACE-01.0 steps 1-2: the ready lot and what the other misses.
        var check = await (await sale.Manager.Client.GetInvoicePublishCheckAsync(invoice.Id())).ReadAsync(HttpStatusCode.OK);
        Assert.Equal((invoice.Text("code"), true), (check.Text("invoiceCode"), check.GetProperty("getsQrCode").GetBoolean()));
        var checkedLots = check.LotsById();
        Assert.Equal(("NotPublished", 0, JsonValueKind.Null), (checkedLots[sale.LotId].Text("originState"), checkedLots[sale.LotId].GetProperty("missing").GetArrayLength(),
            checkedLots[sale.LotId].GetProperty("messageCode").ValueKind));
        Assert.Equal(("NotPublished", "MSG-TRACE-01", NotReady(otherCode, missing)), (checkedLots[sale.OtherLotId].Text("originState"), checkedLots[sale.OtherLotId].Text("messageCode"),
            checkedLots[sale.OtherLotId].Text("message")));
        Assert.Equal(HarvestLotOriginState.NotPublished, (await api.LotRowAsync(sale.LotId)).OriginState);
        Assert.Empty(await api.PublishAuditAsync(sale.LotId));

        var finished = await sale.Manager.Client.FinishedAsync(invoice.Id(), wantsQrCode: true);

        var lots = finished.LotsById();
        Assert.Equal(("Published", "NotPublished"), (lots[sale.LotId].Text("originState"), lots[sale.OtherLotId].Text("originState")));
        Assert.Equal(["NoSeedingSession 2"], lots[sale.OtherLotId].Missing());
        Assert.Equal(("MSG-TRACE-01", NotReady(otherCode, missing)), (lots[sale.OtherLotId].Text("messageCode"), lots[sale.OtherLotId].Text("message")));
        var qrUrl = finished.Text("qrUrl");
        Assert.NotNull(qrUrl);
        Assert.EndsWith($"Origin: {qrUrl}", Assert.Single(api.SmsTo(invoice.GetProperty("customer").Text("phoneNumber"))).Text);

        // POST-1, POST-3: the lot is Published with its publish time; one audit row names the invoice; the lot left out has none.
        Assert.Equal(HarvestLotOriginState.Published, (await api.LotRowAsync(sale.LotId)).OriginState);
        Assert.Equal(HarvestLotOriginState.NotPublished, (await api.LotRowAsync(sale.OtherLotId)).OriginState);
        var audit = Assert.Single(await api.PublishAuditAsync(sale.LotId));
        Assert.Equal((ProvenancePublishTestKit.PublishedAction, code, (Guid?)sale.Manager.Account.Id), (audit.Action, audit.RecordCode, audit.ActorAccountId));
        Assert.Contains($"\"SalesInvoiceCode\":\"{invoice.Text("code")}\"", audit.After);
        Assert.Empty(await api.PublishAuditAsync(sale.OtherLotId));
        var finishAudit = await api.QueryAsync(db => db.AuditEvents.AsNoTracking().SingleAsync(e => e.RecordId == invoice.Id() && e.Action == "SalesInvoice.SaleFinished"));
        Assert.Contains($"\"PublishedLots\":[\"{code}\"]", finishAudit.After);
        var other = await sale.Manager.Client.GetHarvestLotAsync(sale.OtherLotId);
        Assert.Equal("NotPublished", other.Text("originState"));
        Assert.Equal(["NoSeedingSession 2"], other.Missing("originMissing"));

        // BR-TRACE-02: published once. A later invoice that sells the Published lot gets its QR code without publishing it again.
        var publishedAt = (await api.LotRowAsync(sale.LotId)).PublishedAt;
        var later = await sale.Manager.Client.SoldAsync(sale, sale.LotId);
        var laterFinished = await sale.Manager.Client.FinishedAsync(later.Id(), wantsQrCode: true);
        Assert.NotNull(laterFinished.Text("qrUrl"));
        Assert.Equal("Published", laterFinished.LotsById()[sale.LotId].Text("originState"));
        Assert.Single(await api.PublishAuditAsync(sale.LotId));
        Assert.Equal(publishedAt, (await api.LotRowAsync(sale.LotId)).PublishedAt);
    }

    // TRACE-01.0.E1 and SALE-05.11: no lot passes the check, so nothing is published and the invoice has no QR code; a No publishes nothing either (SALE-05.10).
    [Fact]
    public async Task No_lot_is_published_without_a_yes_or_when_none_has_all_its_origin_information()
    {
        using var api = new AuthApiFactory();
        var sale = await api.SaleSetupAsync();
        var refusedNo = await sale.Manager.Client.SoldAsync(sale, sale.LotId);
        Assert.Equal(0, (await sale.Manager.Client.FinishedAsync(refusedNo.Id(), wantsQrCode: false)).GetProperty("lots").GetArrayLength());
        Assert.Equal(HarvestLotOriginState.NotPublished, (await api.LotRowAsync(sale.LotId)).OriginState);

        await api.LeaveLaneWithoutSeedLotAsync(sale.RaftId, 1);
        await api.LeaveLaneWithoutSeedLotAsync(sale.RaftId, 2);
        var invoice = (await sale.Manager.Client.SellAsync(Invoice(sale.CustomerId, [Line(sale.ProductId, sale.LotId, 1m), Line(sale.ProductId, sale.OtherLotId, 1m)], Cash(60_000m))))
            .GetProperty("invoice");
        var check = await (await sale.Manager.Client.GetInvoicePublishCheckAsync(invoice.Id())).ReadAsync(HttpStatusCode.OK);
        Assert.False(check.GetProperty("getsQrCode").GetBoolean());

        var finished = await sale.Manager.Client.FinishedAsync(invoice.Id(), wantsQrCode: true);

        Assert.Equal(JsonValueKind.Null, finished.GetProperty("qrUrl").ValueKind);
        Assert.All(finished.GetProperty("lots").EnumerateArray(), l => Assert.Equal(("NotPublished", "MSG-TRACE-01"), (l.Text("originState"), l.Text("messageCode"))));
        Assert.Empty(await api.PublishAuditAsync(sale.LotId));
        Assert.Empty(await api.PublishAuditAsync(sale.OtherLotId));
        Assert.Equal("Sent", finished.Text("deliveryStatus"));
    }

    // BR-TRACE-02: the end of a sale publishes only lots that are Not published: a Withdrawn lot stays Withdrawn, also when the Owner ends the sale (only the Trace lot screen
    // publishes it again). The ready lot beside it gives the QR code.
    [Fact]
    public async Task The_end_of_a_sale_never_publishes_a_withdrawn_lot_again()
    {
        using var api = new AuthApiFactory();
        var sale = await api.SaleSetupAsync();
        await api.WithdrawAsync(sale.LotId);
        var withdrawn = await api.LotRowAsync(sale.LotId);
        var invoice = (await sale.Owner.Client.SellAsync(Invoice(sale.CustomerId, [Line(sale.ProductId, sale.LotId, 1m), Line(sale.ProductId, sale.OtherLotId, 1m)], Cash(60_000m))))
            .GetProperty("invoice");

        var finished = await sale.Owner.Client.FinishedAsync(invoice.Id(), wantsQrCode: true);

        var lots = finished.LotsById();
        Assert.Equal(("Withdrawn", "Published"), (lots[sale.LotId].Text("originState"), lots[sale.OtherLotId].Text("originState")));
        Assert.Empty(lots[sale.LotId].Missing());
        Assert.NotNull(finished.Text("qrUrl"));
        Assert.Equal(withdrawn.PublishedAt, (await api.LotRowAsync(sale.LotId)).PublishedAt);
        Assert.Empty(await api.PublishAuditAsync(sale.LotId));
    }

    // The invoice zone may differ from the lot's zone (BR-SALE-08): a Zone Manager ends the sale of their zone, but a lot of a zone where they do not publish stays Not published
    // and says who can publish it.
    [Fact]
    public async Task A_lot_of_another_zone_is_not_published_by_a_zone_manager_who_does_not_publish_there()
    {
        using var api = new AuthApiFactory();
        var sale = await api.SaleSetupAsync();
        var away = await api.SeededRaftAsync();
        var awayLot = await away.Owner.Client.HarvestLotAsync(away.RaftId, HarvestFields.OfLanes(1));
        var body = Invoice(sale.CustomerId, [Line(sale.ProductId, sale.LotId, 1m), Line(sale.ProductId, awayLot.Id(), 1m)], Cash(60_000m)).With("zoneId", sale.ZoneId);
        var invoice = (await sale.Owner.Client.SellAsync(body)).GetProperty("invoice");

        var finished = await sale.Manager.Client.FinishedAsync(invoice.Id(), wantsQrCode: true);

        var lots = finished.LotsById();
        Assert.Equal(("Published", "NotPublished"), (lots[sale.LotId].Text("originState"), lots[awayLot.Id()].Text("originState")));
        var zoneCode = awayLot.Text("zoneCode");
        Assert.Equal([$"NotPublisherOfZone {zoneCode}"], lots[awayLot.Id()].Missing());
        Assert.Equal(NotReady(awayLot.Text("lotCode"), $"only a Zone Manager of zone {zoneCode} or the Cooperative Owner can publish it"), lots[awayLot.Id()].Text("message"));
        Assert.Empty(await api.PublishAuditAsync(awayLot.Id()));
    }
}
