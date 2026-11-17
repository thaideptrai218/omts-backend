using System.Net;
using ClosedXML.Excel;
using OMTS.Api.IntegrationTests.Cultivation;
using OMTS.Api.IntegrationTests.Farming;
using OMTS.Api.IntegrationTests.Harvest;
using OMTS.Api.IntegrationTests.Infrastructure;
using OMTS.Api.IntegrationTests.Sales;
using static OMTS.Api.IntegrationTests.Farming.FarmingTestKit;
using static OMTS.Api.IntegrationTests.Reports.SalesPerformanceTestKit;
using static OMTS.Api.IntegrationTests.Sales.SalesInvoiceChangeTestKit;
using static OMTS.Api.IntegrationTests.Sales.SalesInvoiceTestKit;
using static OMTS.Api.IntegrationTests.Sales.SalesReturnTestKit;

namespace OMTS.Api.IntegrationTests.Reports;

/// <summary>REPORT-02 on SQL Server: the figures, the lines of a period and the file are translated to SQL, leave out the cancelled and the returned invoices, and agree with each other.</summary>
public class PriceTrendsSqlServerTests
{
    [SqlServerFact]
    public async Task The_figures_the_lines_and_the_export_run_on_sql_server_and_leave_out_cancelled_and_returned_invoices()
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

        async Task<Guid> NewInvoiceAsync(decimal kg, decimal unitPrice, decimal discount = 0m) =>
            (await owner.SellAsync(Invoice(customerId, [Line(productId, lotId, kg, unitPrice, priceDecidedByOwner: true)], onDebt, lotId).With("discount", discount == 0m ? null : discount)
                .With("discountDecidedByOwner", discount != 0m))).GetProperty("invoice").Id();

        // 10 kg at 30,000 and 30 kg at 20,000 count; 4 kg at 50,000 is returned and 2 kg at 90,000 is cancelled, so neither counts.
        await NewInvoiceAsync(10m, 30_000m);
        await NewInvoiceAsync(30m, 20_000m, discount: 50_000m);
        var returned = await NewInvoiceAsync(4m, 50_000m);
        var cancelled = await NewInvoiceAsync(2m, 90_000m);
        await owner.ReturnedAsync(returned, ReturnBody(await owner.InvoiceAsync(returned), "BackForSale"));
        await owner.CancelledAsync(cancelled, Cancellation(await owner.InvoiceAsync(cancelled)));

        foreach (var unit in new[] { "day", "week", "month" })
        {
            var report = await owner.PriceTrendsAsync($"unit={unit}");
            Assert.Equal((40m, 22_500m, 20_000m, 30_000m), Assert.Single(Assert.Single(report.TrendSeries()).TrendPeriods()).PriceFigures());
        }

        var key = Day(PurchaseRequestTestKit.Today);
        var lines = await (await owner.GetPriceTrendLinesAsync($"productId={productId}&periodKey={key}")).ReadAsync(HttpStatusCode.OK);
        Assert.Equal([20_000m, 30_000m], lines.TrendLines().Select(l => l.Money("unitPrice")).Order());

        var file = await owner.GetPriceTrendsExportAsync($"productIds={productId}&zoneIds={zoneId}");
        Assert.Equal(HttpStatusCode.OK, file.StatusCode);
        using var book = new XLWorkbook(await file.Content.ReadAsStreamAsync());
        Assert.Equal(22_500d, book.Worksheet("Price trends").Cell(2, 4).GetDouble());
    }
}
