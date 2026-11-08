using System.Net;
using System.Text.Json;
using OMTS.Api.IntegrationTests.Farming;
using OMTS.Api.IntegrationTests.Harvest;
using OMTS.Api.IntegrationTests.Infrastructure;
using OMTS.Domain.Enums;
using static OMTS.Api.IntegrationTests.Sales.SalesInvoiceSendingTestKit;
using static OMTS.Api.IntegrationTests.Sales.SalesInvoiceTestKit;

namespace OMTS.Api.IntegrationTests.Traceability;

/// <summary>TRACE-01.2 on the Trace lot screen: the check of one lot (BR-TRACE-01, MSG-TRACE-01), publish once (BR-TRACE-02), the republish of a Withdrawn lot by the Owner only, and the audit row.</summary>
public class ProvenanceLotPublishTests
{
    private static string Confirm(string action) => $"Are you sure you want to {action}? This action is recorded in the audit log.";

    // PRE-2 and POST-1, POST-3: a lot on no invoice is not offered; once sold it is published once, with one audit row; a second publish is refused and changes nothing.
    [Fact]
    public async Task A_lot_on_a_sales_invoice_is_published_once_and_the_publishing_is_audited()
    {
        using var api = new AuthApiFactory();
        var sale = await api.SaleSetupAsync();
        var code = (await sale.Manager.Client.GetHarvestLotAsync(sale.LotId)).Text("lotCode");

        var unsold = await sale.Manager.Client.PublishCheckAsync(sale.LotId);
        Assert.Equal(("NotPublished", false, "NotOnSalesInvoice", true, JsonValueKind.Null), (unsold.Text("originState"), unsold.GetProperty("canPublish").GetBoolean(),
            unsold.Text("block"), unsold.GetProperty("ready").GetBoolean(), unsold.GetProperty("message").ValueKind));
        var refused = await (await sale.Manager.Client.PostPublishAsync(sale.LotId)).ReadAsync(HttpStatusCode.Conflict);
        Assert.Equal(("MSG-GEN-09", $"Harvest lot {code} is on no sales invoice and cannot be used for this action."), (refused.Text("code"), refused.Text("detail")));
        Assert.False((await sale.Manager.Client.GetHarvestLotAsync(sale.LotId)).GetProperty("canPublish").GetBoolean());

        await sale.Manager.Client.SoldAsync(sale, sale.LotId);
        var check = await sale.Manager.Client.PublishCheckAsync(sale.LotId);
        Assert.Equal((true, true, false, "MSG-GEN-04", Confirm($"publish the origin information of harvest lot {code}")), (check.GetProperty("canPublish").GetBoolean(),
            check.GetProperty("onSalesInvoice").GetBoolean(), check.GetProperty("republish").GetBoolean(), check.Text("messageCode"), check.Text("message")));
        Assert.Empty(check.Missing());
        var listed = (await sale.Manager.Client.HarvestScreenAsync(sale.RaftId)).GetProperty("lots").EnumerateArray().Single(l => l.Id() == sale.LotId);
        Assert.Equal((true, 0), (listed.GetProperty("canPublish").GetBoolean(), listed.GetProperty("originMissing").GetArrayLength()));

        var published = await (await sale.Manager.Client.PostPublishAsync(sale.LotId)).ReadAsync(HttpStatusCode.OK);

        var lot = published.GetProperty("lot");
        Assert.Equal(("Published", false, false, "MSG-GEN-05"), (lot.Text("originState"), lot.GetProperty("canPublish").GetBoolean(), published.GetProperty("republished").GetBoolean(),
            published.Text("messageCode")));
        var publishedAt = (await api.LotRowAsync(sale.LotId)).PublishedAt;
        Assert.NotNull(publishedAt);
        var audit = Assert.Single(await api.PublishAuditAsync(sale.LotId));
        Assert.Equal((ProvenancePublishTestKit.PublishedAction, code, (Guid?)sale.ZoneId, (Guid?)sale.Manager.Account.Id), (audit.Action, audit.RecordCode, audit.ZoneId, audit.ActorAccountId));
        Assert.Contains("\"OriginState\":\"NotPublished\"", audit.Before);
        Assert.Contains("\"OriginState\":\"Published\"", audit.After);
        Assert.Contains("\"SalesInvoiceCode\":null", audit.After);

        // BR-TRACE-02: published once. The Owner too is refused, and nothing is written again.
        foreach (var client in new[] { sale.Manager.Client, sale.Owner.Client })
        {
            var again = await (await client.PostPublishAsync(sale.LotId)).ReadAsync(HttpStatusCode.Conflict);
            Assert.Equal(("MSG-GEN-09", $"Harvest lot {code} is Published and cannot be used for this action."), (again.Text("code"), again.Text("detail")));
        }

        Assert.Single(await api.PublishAuditAsync(sale.LotId));
        Assert.Equal(publishedAt, (await api.LotRowAsync(sale.LotId)).PublishedAt);
        Assert.Equal(("AlreadyPublished", JsonValueKind.Null), ((await sale.Owner.Client.PublishCheckAsync(sale.LotId)).Text("block"),
            (await sale.Owner.Client.PublishCheckAsync(sale.LotId)).GetProperty("message").ValueKind));
    }

    // TRACE-01.0.E1 with BR-TRACE-01: a lane with no seeding session, or whose seed lot is on a cancelled receipt, is named; the lot stays Not published and nothing is written.
    [Fact]
    public async Task A_lot_missing_origin_information_is_refused_with_msg_trace_01_naming_what_is_missing()
    {
        using var api = new AuthApiFactory();
        var sale = await api.SaleSetupAsync();
        await sale.Manager.Client.SoldAsync(sale, sale.LotId);
        await sale.Manager.Client.SoldAsync(sale, sale.OtherLotId);
        await api.LeaveLaneWithoutSeedLotAsync(sale.RaftId, 2);
        var seedLot = await api.CancelSeedReceiptAsync(sale.RaftId);
        var expected = new Dictionary<Guid, (string Item, string Text)>
        {
            [sale.LotId] = ($"SeedLotNotOnCompletedReceipt 1 {seedLot}", $"the seed lot {seedLot} of lane 1 is not on a completed purchase receipt"),
            [sale.OtherLotId] = ("NoSeedingSession 2", "lane 2 has no seeding session in the cycle of the lot"),
        };

        foreach (var (lotId, missing) in expected)
        {
            var view = await sale.Manager.Client.GetHarvestLotAsync(lotId);
            var code = view.Text("lotCode");
            Assert.Equal([missing.Item], view.Missing("originMissing"));
            Assert.True(view.GetProperty("canPublish").GetBoolean());
            var check = await sale.Manager.Client.PublishCheckAsync(lotId);
            Assert.Equal((false, true, "MSG-TRACE-01", $"Harvest lot {code} cannot be published yet: {missing.Text}."),
                (check.GetProperty("ready").GetBoolean(), check.GetProperty("canPublish").GetBoolean(), check.Text("messageCode"), check.Text("message")));
            Assert.Equal([missing.Item], check.Missing());

            var refused = await (await sale.Manager.Client.PostPublishAsync(lotId)).ReadAsync(HttpStatusCode.Conflict);

            Assert.Equal(("MSG-TRACE-01", code, $"Harvest lot {code} cannot be published yet: {missing.Text}."), (refused.Text("code"), refused.Text("lotCode"), refused.Text("detail")));
            Assert.Equal([missing.Item], refused.Missing());
            Assert.Equal(HarvestLotOriginState.NotPublished, (await api.LotRowAsync(lotId)).OriginState);
            Assert.Empty(await api.PublishAuditAsync(lotId));
        }
    }

    // BR-TRACE-02 and TRACE-01.2 step 3: a Withdrawn lot is published again by the Owner only; it gets a new publish time, and its withdrawal stays on the lot and in the log.
    [Fact]
    public async Task Only_the_owner_publishes_a_withdrawn_lot_again()
    {
        using var api = new AuthApiFactory();
        var sale = await api.SaleSetupAsync();
        await sale.Manager.Client.SoldAsync(sale, sale.LotId);
        await api.WithdrawAsync(sale.LotId);
        var withdrawn = await api.LotRowAsync(sale.LotId);
        var code = withdrawn.LotCode;

        var managerCheck = await sale.Manager.Client.PublishCheckAsync(sale.LotId);
        Assert.Equal(("Withdrawn", false, "OwnerOnly", true), (managerCheck.Text("originState"), managerCheck.GetProperty("canPublish").GetBoolean(), managerCheck.Text("block"),
            managerCheck.GetProperty("republish").GetBoolean()));
        Assert.False((await sale.Manager.Client.GetHarvestLotAsync(sale.LotId)).GetProperty("canPublish").GetBoolean());
        var refused = await sale.Manager.Client.PostPublishAsync(sale.LotId);
        Assert.Equal((HttpStatusCode.Forbidden, "MSG-GEN-08"), (refused.StatusCode, await refused.ErrorCodeAsync()));
        Assert.Equal(withdrawn.PublishedAt, (await api.LotRowAsync(sale.LotId)).PublishedAt);

        var ownerCheck = await sale.Owner.Client.PublishCheckAsync(sale.LotId);
        Assert.Equal((true, Confirm($"publish again the origin information of harvest lot {code}")), (ownerCheck.GetProperty("canPublish").GetBoolean(), ownerCheck.Text("message")));
        Assert.True((await sale.Owner.Client.GetHarvestLotAsync(sale.LotId)).GetProperty("canPublish").GetBoolean());

        var republished = await (await sale.Owner.Client.PostPublishAsync(sale.LotId)).ReadAsync(HttpStatusCode.OK);

        Assert.Equal(("Published", true), (republished.GetProperty("lot").Text("originState"), republished.GetProperty("republished").GetBoolean()));
        var row = await api.LotRowAsync(sale.LotId);
        Assert.Equal((withdrawn.WithdrawnAt, withdrawn.WithdrawalReason, HarvestLotOriginState.Published), (row.WithdrawnAt, row.WithdrawalReason, row.OriginState));
        Assert.True(row.PublishedAt > row.WithdrawnAt);
        var audit = Assert.Single(await api.PublishAuditAsync(sale.LotId));
        Assert.Equal((ProvenancePublishTestKit.RepublishedAction, (Guid?)sale.Owner.Account.Id), (audit.Action, audit.ActorAccountId));
        Assert.Contains("\"OriginState\":\"Withdrawn\"", audit.Before);
    }

    // PRE-2: a lot whose only invoice is cancelled is on no current invoice: the sale did not happen, so the lot is not offered (PM default).
    [Fact]
    public async Task A_lot_only_on_a_cancelled_invoice_is_not_published_from_the_lot_screen()
    {
        using var api = new AuthApiFactory();
        var sale = await api.SaleSetupAsync();
        var invoice = await sale.Manager.Client.SoldAsync(sale, sale.LotId);
        await api.CancelInvoiceAsync(invoice.Id());

        var check = await sale.Owner.Client.PublishCheckAsync(sale.LotId);
        Assert.Equal((false, false, "NotOnSalesInvoice"), (check.GetProperty("onSalesInvoice").GetBoolean(), check.GetProperty("canPublish").GetBoolean(), check.Text("block")));
        Assert.Equal("MSG-GEN-09", await (await sale.Owner.Client.PostPublishAsync(sale.LotId)).ErrorCodeAsync());
        Assert.Empty(await api.PublishAuditAsync(sale.LotId));
    }
}
