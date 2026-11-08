using System.Diagnostics;
using Microsoft.EntityFrameworkCore;
using OMTS.Api.IntegrationTests.Cultivation;
using OMTS.Api.IntegrationTests.Farming;
using OMTS.Api.IntegrationTests.Infrastructure;
using OMTS.Domain.Entities.Harvest;
using Xunit.Abstractions;
using static OMTS.Api.IntegrationTests.Farming.FarmingTestKit;
using static OMTS.Api.IntegrationTests.Harvest.HarvestYieldTestKit;

namespace OMTS.Api.IntegrationTests.Harvest;

/// <summary>
/// QA of the HARVEST-04 query shape on SQL Server: a period of up to five years can hold more lots than the 2,100 parameters of one SQL Server command, so the lot ids and the
/// zone ids must not be sent one parameter each; and the report reads the lots and their sales in a fixed number of queries, not one per lot.
/// </summary>
public class HarvestYieldQaTestsSqlServer(ITestOutputHelper output)
{
    private const int ExtraLots = 2_200;

    [SqlServerFact]
    public async Task A_report_of_more_than_2100_lots_answers_on_sql_server_with_every_lot()
    {
        await using var host = await FarmingSqlServerHost.StartAsync();
        var owner = host.Owner.Client;
        var zone = (await owner.CreateZoneAsync()).Id();
        var raft = (await owner.CreateRaftAsync(zone)).Id();
        var cycle = (await owner.StartCycleAsync(raft, new CycleBody(Today.AddDays(-10)))).Id();
        await owner.SeedingRecordAsync(cycle, (await owner.SeedLotAsync()).Session([1, 2, 3]));
        var model = (await owner.HarvestLotAsync(raft)).Id();
        var source = await host.QueryAsync(db => db.HarvestLots.AsNoTracking().SingleAsync(l => l.Id == model));
        await host.UseDbAsync(db => db.HarvestLots.AddRange(Enumerable.Range(1, ExtraLots).Select(n => new HarvestLot
        {
            LotCode = $"{source.LotCode}-QA{n}",
            RaftId = source.RaftId,
            CycleId = source.CycleId,
            HarvestedAt = source.HarvestedAt,
            HarvestDate = source.HarvestDate,
            CageCount = source.CageCount,
            KgPerCage = source.KgPerCage,
            EstimatedKg = 1.001m,
            RecordedByAccountId = source.RecordedByAccountId,
            StatusId = source.StatusId,
        })));
        var manager = await host.StaffInZoneAsync(zone);

        var watch = Stopwatch.StartNew();
        var byCycle = await manager.Client.YieldAsync(YieldFilters(groupBy: "cycle", zoneIds: [zone]));
        var byRaft = await owner.YieldAsync(YieldFilters(groupBy: "raft"));
        output.WriteLine($"two reports of {ExtraLots + 1} lots: {watch.ElapsedMilliseconds} ms");

        var totals = byCycle.GetProperty("totals");
        Assert.Equal((ExtraLots + 1, 400m + (ExtraLots * 1.001m)), (totals.GetProperty("lotCount").GetInt32(), totals.Kg("estimatedKg")));
        Assert.Equal(ExtraLots + 1, byRaft.YieldLotIds().Count);
        Assert.Equal(System.Net.HttpStatusCode.OK, (await manager.Client.GetYieldExportAsync(YieldFilters(zoneIds: [zone]))).StatusCode);
    }
}
