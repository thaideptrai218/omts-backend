using System.Net;
using Microsoft.EntityFrameworkCore;
using OMTS.Api.IntegrationTests.Cultivation;
using OMTS.Api.IntegrationTests.Farming;
using OMTS.Api.IntegrationTests.Infrastructure;
using static OMTS.Api.IntegrationTests.Farming.FarmingTestKit;
using static OMTS.Api.IntegrationTests.Harvest.HarvestYieldTestKit;
using static OMTS.Api.IntegrationTests.Sales.SalesInvoiceTestKit;
using SalesCatalogue = OMTS.Api.IntegrationTests.Sales.SalesCatalogueTestKit;

namespace OMTS.Api.IntegrationTests.Harvest;

/// <summary>HARVEST-04 on SQL Server: the zone filter, the period and the whole-cycle query translate, and the sold kg come from the real invoices.</summary>
public class HarvestYieldSqlServerTests
{
    [SqlServerFact]
    public async Task The_zone_period_and_whole_cycle_query_runs_on_sql_server_with_the_real_sales()
    {
        await using var host = await FarmingSqlServerHost.StartAsync();
        var owner = host.Owner.Client;
        var zoneA = (await owner.CreateZoneAsync()).Id();
        var zoneB = (await owner.CreateZoneAsync()).Id();
        var raftA = (await owner.CreateRaftAsync(zoneA)).Id();
        var raftB = (await owner.CreateRaftAsync(zoneB)).Id();
        var seed = await owner.SeedLotAsync();
        foreach (var raft in new[] { raftA, raftB })
        {
            var cycle = (await owner.StartCycleAsync(raft, new CycleBody(Today.AddDays(-10)))).Id();
            await owner.SeedingRecordAsync(cycle, seed.Session([1, 2, 3]));
        }

        var productId = (await SalesCatalogue.CreateProductAsync(owner, unitPrice: ListPrice)).Id();
        var customerId = (await SalesCatalogue.CreateCustomerAsync(owner)).Id();
        var manager = await host.StaffInZoneAsync(zoneA);
        var inPeriod = (await manager.Client.HarvestLotAsync(raftA, HarvestFields.OfLanes(1))).Id();
        var later = (await manager.Client.HarvestLotAsync(raftA, new HarvestFields { Lanes = ["2"], Cages = "3" })).Id();
        var otherZone = (await owner.HarvestLotAsync(raftB, HarvestFields.OfLanes(1))).Id();
        await manager.Client.SellAsync(Invoice(customerId, [Line(productId, inPeriod, 123.456m)], Cash(3_703_680m), inPeriod));
        var september = new DateOnly(2026, 9, 1);
        foreach (var (lot, date) in new[] { (inPeriod, september), (later, new DateOnly(2026, 10, 3)), (otherZone, september) })
        {
            await host.QueryAsync(db => db.HarvestLots.Where(l => l.Id == lot).ExecuteUpdateAsync(s => s.SetProperty(l => l.HarvestDate, date)));
        }

        var period = YieldFilters(september, new DateOnly(2026, 9, 30), "cycle", zoneIds: [zoneA]);
        var byCycle = await owner.YieldAsync(period);

        var group = Assert.Single(byCycle.Groups());
        Assert.Equal(new[] { inPeriod, later }.Order(), byCycle.YieldLotIds().Order());
        Assert.Equal((550m, 123.456m, 0m, 426.544m, true), group.Figures());
        Assert.Equal([inPeriod], (await manager.Client.YieldAsync(YieldFilters(september, new DateOnly(2026, 9, 30)))).YieldLotIds());
        Assert.Equal(2, (await owner.YieldAsync(YieldFilters(september, new DateOnly(2026, 9, 30), "zone"))).Groups().Count);
        Assert.Equal(HttpStatusCode.OK, (await manager.Client.GetYieldExportAsync(period)).StatusCode);
    }
}
