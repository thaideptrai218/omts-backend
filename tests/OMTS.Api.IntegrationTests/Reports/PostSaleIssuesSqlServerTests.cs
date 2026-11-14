using System.Net;
using ClosedXML.Excel;
using OMTS.Api.IntegrationTests.Cultivation;
using OMTS.Api.IntegrationTests.Farming;
using OMTS.Api.IntegrationTests.Harvest;
using OMTS.Api.IntegrationTests.Infrastructure;
using OMTS.Api.IntegrationTests.Sales;
using OMTS.Domain.Constants;
using OMTS.Domain.Entities.Sales;
using static OMTS.Api.IntegrationTests.Farming.FarmingTestKit;
using static OMTS.Api.IntegrationTests.Reports.PostSaleIssuesTestKit;
using static OMTS.Api.IntegrationTests.Reports.SalesPerformanceTestKit;
using static OMTS.Api.IntegrationTests.Sales.SalesInvoiceChangeTestKit;
using static OMTS.Api.IntegrationTests.Sales.SalesInvoiceTestKit;
using static OMTS.Api.IntegrationTests.Sales.SalesReturnTestKit;

namespace OMTS.Api.IntegrationTests.Reports;

/// <summary>REPORT-03 on SQL Server: the four kinds of event, every grouping, the events of a group and the file are translated to SQL and agree with each other.</summary>
public class PostSaleIssuesSqlServerTests
{
    [SqlServerFact]
    public async Task Every_grouping_the_events_of_a_group_and_the_export_run_on_sql_server_and_count_each_event_once()
    {
        await using var host = await FarmingSqlServerHost.StartAsync();
        var owner = host.Owner.Client;
        var zoneId = (await owner.CreateZoneAsync()).Id();
        var raftId = (await owner.CreateRaftAsync(zoneId)).Id();
        var cycleId = (await owner.StartCycleAsync(raftId, new CycleBody(Today.AddDays(-10)))).Id();
        var seed = await owner.SeedLotAsync();
        await owner.SeedingRecordAsync(cycleId, seed.Session([1, 2, 3, 4, 5]));
        var productId = (await owner.CreateProductAsync(unitPrice: ListPrice)).Id();
        var customerId = (await owner.CreateCustomerAsync()).Id();
        var lotId = (await owner.HarvestLotAsync(raftId, HarvestFields.OfLanes(1))).Id();
        var onDebt = Cash(0m, "PayLater", payLaterOwnerAgreed: true);

        async Task<Guid> NewInvoiceAsync(decimal kg) =>
            (await owner.SellAsync(Invoice(customerId, [Line(productId, lotId, kg, 50_000m, priceDecidedByOwner: true)], onDebt, lotId))).GetProperty("invoice").Id();

        // A claim on the first invoice; a price cut of 20,000 on the second; the third (2 kg, 100,000) returned; the fourth (50,000) cancelled.
        var claimed = await NewInvoiceAsync(5m);
        var cut = await NewInvoiceAsync(4m);
        var returned = await NewInvoiceAsync(2m);
        var cancelled = await NewInvoiceAsync(1m);
        await host.UseDbAsync(db => db.QualityClaims.Add(new QualityClaim
        {
            Code = "KN" + AcsTestKit.NewCode(), InvoiceId = claimed, CustomerId = customerId, SentAt = DateTimeOffset.UtcNow, Description = "Oysters arrived dead",
            // An accepted claim carries its offer and who answered it (CK_QualityClaim_Accepted_Has_Offer, CK_QualityClaim_Answered_Has_Response).
            StatusId = StatusIds.QualityClaim.Accepted, Offer = OMTS.Domain.Enums.QualityClaimOffer.PriceCutWholeInvoice,
            RespondedAt = DateTimeOffset.UtcNow, RespondedByAccountId = host.Owner.Account.Id,
        }));
        await owner.CorrectedAsync(cut, PriceCut(await owner.InvoiceAsync(cut), 20_000m));
        // Thrown-away kg ask MSG-HARVEST-02 about the lot: it stays for sale, the cancelled invoice still sells from it.
        await owner.ReturnedAsync(
            returned, ReturnBody(await owner.InvoiceAsync(returned), "ThrownAway").With("thrownAwayLots", new[] { new { harvestLotId = lotId, stillForSale = true } }));
        await owner.CancelledAsync(cancelled, Cancellation(await owner.InvoiceAsync(cancelled)));

        foreach (var grouping in new[] { "customer", "zone", "lot" })
        {
            var report = await owner.PostSaleAsync($"groupBy={grouping}");
            var totals = report.Totals();
            Assert.Equal((1, 1, 1, 1), totals.EventCounts());
            Assert.Equal((1, 2m, 2m, 100_000m, 20_000m, 50_000m), (totals.GetProperty("claimsAccepted").GetInt32(), totals.Money("returnedKg"), totals.Money("discardedKg"), totals.Money("returnsValue"), totals.Money("priceCutsAmount"), totals.Money("cancelledTotal")));
            Assert.Equal(2m, Assert.Single(report.Groups()).Money("returnedKg"));
        }

        var byCustomer = await owner.PostSaleAsync();
        Assert.Equal((1, 1, 1, 1), byCustomer.Group(customerId.ToString()).EventCounts());
        var byLot = await owner.PostSaleAsync("groupBy=lot");
        Assert.Equal((1, 1, 1, 1), byLot.Group(lotId.ToString()).EventCounts());
        Assert.Equal(System.Text.Json.JsonValueKind.Null, byLot.Group(lotId.ToString()).GetProperty("priceCutsAmount").ValueKind);

        foreach (var grouping in new[] { ("customer", customerId), ("zone", zoneId), ("lot", lotId) })
        {
            var items = await (await owner.GetPostSaleItemsAsync($"groupBy={grouping.Item1}&groupKey={grouping.Item2}")).ReadAsync(HttpStatusCode.OK);
            Assert.Equal(["cancellation", "claim", "priceCut", "return"], items.PostSaleItems().Select(i => i.Text("type")).Order(StringComparer.Ordinal));
        }

        var file = await owner.GetPostSaleExportAsync("groupBy=customer");
        Assert.Equal(HttpStatusCode.OK, file.StatusCode);
        using var book = new XLWorkbook(await file.Content.ReadAsStreamAsync());
        Assert.Equal(50_000d, book.Worksheet("Post-sale issues").Rows().Last(r => !r.IsEmpty()).Cell(13).GetDouble());
    }
}
