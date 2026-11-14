using OMTS.Api.IntegrationTests.Cultivation;
using OMTS.Api.IntegrationTests.Farming;
using OMTS.Api.IntegrationTests.Harvest;
using OMTS.Api.IntegrationTests.Infrastructure;
using OMTS.Api.IntegrationTests.Sales;
using OMTS.Application.Features.Dashboard;
using static OMTS.Api.IntegrationTests.Dashboard.OperationsDashboardTestKit;
using static OMTS.Api.IntegrationTests.Farming.FarmingTestKit;
using static OMTS.Api.IntegrationTests.Sales.SalesInvoiceSearchTestKit;
using static OMTS.Api.IntegrationTests.Sales.SalesInvoiceTestKit;

namespace OMTS.Api.IntegrationTests.Dashboard;

/// <summary>FARM-06 on SQL Server: the sum and count of the revenue, the open lots and the pending-task queries all translate, with and without the filters.</summary>
public class OperationsDashboardSqlServerTests
{
    [SqlServerFact]
    public async Task Every_tile_translates_and_a_sale_shows_in_the_revenue_and_the_open_lots()
    {
        await using var host = await FarmingSqlServerHost.StartAsync();
        var owner = host.Owner.Client;
        var zoneId = (await owner.CreateZoneAsync()).Id();
        var raftId = (await owner.CreateRaftAsync(zoneId)).Id();
        var cycleId = (await owner.StartCycleAsync(raftId, new CycleBody(FarmingTestKit.Today.AddDays(-10)))).Id();
        await owner.SeedingRecordAsync(cycleId, (await owner.SeedLotAsync()).Session([1, 2, 3, 4, 5]));
        var lotId = (await owner.HarvestLotAsync(raftId, HarvestFields.OfLanes(1))).Id();
        var productId = (await owner.CreateProductAsync(unitPrice: ListPrice)).Id();
        var customerId = (await owner.CreateCustomerAsync()).Id();
        var manager = (await host.StaffInZoneAsync(zoneId)).Client;

        var empty = await owner.DashboardAsync();
        Assert.Equal((0m, 0, 1), (empty.RevenueTotal(), empty.RevenueCount(), empty.OpenLots()));

        await manager.SellAsync(Invoice(customerId, [Line(productId, lotId, 2m)], Cash(2 * ListPrice), lotId));

        foreach (var query in new[] { "", Query(("zoneId", zoneId)), Query(("from", FarmingTestKit.Today.AddDays(-3)), ("to", FarmingTestKit.Today), ("zoneId", zoneId)) })
        {
            var dashboard = await owner.DashboardAsync(query);
            Assert.Equal((2 * ListPrice, 1, 1, 0m, 0), (dashboard.RevenueTotal(), dashboard.RevenueCount(), dashboard.OpenLots(), dashboard.Receivables(), dashboard.WaterAlerts()));
            Assert.Equal(0, dashboard.PendingTotal());
            // Purchase requests, quality claims, payrolls and employee requests.
            Assert.Equal(4, dashboard.GetProperty("pendingTasks").GetProperty("groups").GetArrayLength());
        }

        Assert.Equal(PendingTaskKinds.PurchaseRequest, (await owner.DashboardAsync()).GetProperty("pendingTasks").GetProperty("groups")[0].GetProperty("kind").GetString());
    }
}
