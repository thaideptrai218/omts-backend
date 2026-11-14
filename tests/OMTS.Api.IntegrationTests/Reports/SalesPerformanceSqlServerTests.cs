using System.Net;
using ClosedXML.Excel;
using OMTS.Api.IntegrationTests.Cultivation;
using OMTS.Api.IntegrationTests.Farming;
using OMTS.Api.IntegrationTests.Harvest;
using OMTS.Api.IntegrationTests.Infrastructure;
using OMTS.Api.IntegrationTests.Sales;
using OMTS.Domain.Constants;
using OMTS.Domain.Entities.Sales;
using OMTS.Domain.Enums;
using static OMTS.Api.IntegrationTests.Farming.FarmingTestKit;
using static OMTS.Api.IntegrationTests.Reports.SalesPerformanceTestKit;
using static OMTS.Api.IntegrationTests.Sales.SalesInvoiceChangeTestKit;
using static OMTS.Api.IntegrationTests.Sales.SalesInvoiceTestKit;
using static OMTS.Api.IntegrationTests.Sales.SalesReturnTestKit;

namespace OMTS.Api.IntegrationTests.Reports;

/// <summary>REPORT-01 on SQL Server: every grouping, the invoices of a group and the file are translated to SQL and agree with each other and with the customer debt.</summary>
public class SalesPerformanceSqlServerTests
{
    [SqlServerFact]
    public async Task Every_grouping_the_invoices_of_a_group_and_the_export_run_on_sql_server_and_add_up_to_the_same_totals()
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

        // 495,000 (10 kg at 50,000, less a discount of 10,000, plus charges of 5,000); 200,000 less a price cut of 20,000, then returned; a walk-in sale of 50,000 in cash.
        await owner.SellAsync(Invoice(customerId, [Line(productId, lotId, 10m, 50_000m)], onDebt, lotId).With("discount", 10_000m).With("otherCharges", 5_000m));
        var second = (await owner.SellAsync(Invoice(customerId, [Line(productId, lotId, 4m, 50_000m)], onDebt, lotId))).GetProperty("invoice");
        await owner.CorrectedAsync(second.Id(), PriceCut(await owner.InvoiceAsync(second.Id()), 20_000m));
        var secondNow = await owner.InvoiceAsync(second.Id());
        await owner.ReturnedAsync(second.Id(), ReturnBody(secondNow, "BackForSale"));
        await owner.SellAsync(Invoice(null, [Line(productId, lotId, 1m, 50_000m)], Cash(50_000m), lotId));
        await host.UseDbAsync(db => db.CustomerPayments.Add(new CustomerPayment
        {
            Code = "PT" + AcsTestKit.NewCode(), CustomerId = customerId, Kind = CustomerPaymentKind.Payment, Amount = 100_000m, Method = CustomerPaymentMethod.OtherTransfer,
            PaymentDate = PurchaseRequestTestKit.Today, StatusId = StatusIds.CustomerPayment.Completed,
        }));

        foreach (var grouping in new[] { "customer", "product", "zone", "day", "week", "month" })
        {
            var report = await owner.ReportAsync($"groupBy={grouping}");
            var totals = report.Totals();
            Assert.Equal((725_000m, 20_000m, 180_000m, 545_000m), (totals.Money("revenue"), totals.Money("priceCuts"), totals.Money("salesReturns"), totals.Money("netRevenue")));
            foreach (var figure in new[] { "revenue", "priceCuts", "salesReturns", "netRevenue", "invoicedKg", "soldKg" })
            {
                Assert.Equal(totals.Money(figure), report.SumOf(figure));
            }
        }

        // By customer: 100,000 received, and the debt of the profile is the one every debt screen shows (675,000 invoices - 180,000 returned - 100,000 paid).
        var customer = (await owner.ReportAsync("groupBy=customer")).Group(customerId.ToString());
        Assert.Equal((100_000m, 395_000m), (customer.Money("netMoneyReceived"), customer.Money("netDebt")));
        Assert.Equal(await owner.CustomerDebtAsync(customerId), customer.Money("netDebt"));

        var drillDown = await (await owner.GetGroupInvoicesAsync($"groupBy=customer&groupKey={customerId}")).ReadAsync(HttpStatusCode.OK);
        Assert.Equal([0m, 495_000m], drillDown.GetProperty("invoices").EnumerateArray().Select(i => i.Money("net")).Order());
        var byProduct = await (await owner.GetGroupInvoicesAsync($"groupBy=product&groupKey={productId}")).ReadAsync(HttpStatusCode.OK);
        Assert.Equal(3, byProduct.GetProperty("invoices").GetArrayLength());

        var file = await owner.GetExportAsync("groupBy=customer");
        Assert.Equal(HttpStatusCode.OK, file.StatusCode);
        using var book = new XLWorkbook(await file.Content.ReadAsStreamAsync());
        Assert.Equal(725_000d, book.Worksheet("Sales performance").Rows().Last(r => !r.IsEmpty()).Cell(4).GetDouble());
    }
}
