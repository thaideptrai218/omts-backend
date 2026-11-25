using Microsoft.Extensions.DependencyInjection;
using OMTS.Api.IntegrationTests.Cultivation;
using OMTS.Api.IntegrationTests.Farming;
using OMTS.Api.IntegrationTests.Infrastructure;
using OMTS.Application.Common.Interfaces.Persistence;
using OMTS.Application.Features.Cultivation.Progress;
using static OMTS.Api.IntegrationTests.Farming.FarmingTestKit;

namespace OMTS.Api.IntegrationTests.Harvest;

/// <summary>The batched and narrow reads of the harvest and cultivation screens, as SQL Server translates them: each gives what the read of one record gives.</summary>
public class HarvestBatchedReadsSqlServerTests
{
    [SqlServerFact]
    public async Task The_batched_reads_give_what_the_reads_one_by_one_give()
    {
        await using var host = await FarmingSqlServerHost.StartAsync();
        var owner = host.Owner.Client;
        var zoneId = (await owner.CreateZoneAsync()).Id();
        var raftId = (await owner.CreateRaftAsync(zoneId)).Id();
        var idleRaftId = (await owner.CreateRaftAsync(zoneId)).Id();
        var cycleId = (await owner.StartCycleAsync(raftId, new CycleBody(Today.AddDays(-10)))).Id();
        await owner.SeedingRecordAsync(cycleId, (await owner.SeedLotAsync()).Session([1, 2, 3]));
        var lot = await owner.HarvestLotAsync(raftId, new HarvestFields { Cages = "3" });
        var lotId = lot.Id();

        await using var scope = host.Api.Services.CreateAsyncScope();
        var services = scope.ServiceProvider;
        var cycles = services.GetRequiredService<ICultivationCycleQuery>();
        var seedings = services.GetRequiredService<ISeedingRecordRepository>();
        var lots = services.GetRequiredService<IHarvestLotRepository>();
        var progress = services.GetRequiredService<ILaneProgressRepository>();
        var rafts = services.GetRequiredService<IRaftRepository>();

        Assert.Equal(cycleId, Assert.Single(await cycles.OpenCyclesAsync([raftId, idleRaftId], default)).Value.Id);
        Assert.Equal(cycleId, Assert.Single(await cycles.CyclesOnDateAsync([raftId, idleRaftId], Today, default)).Value.Id);
        Assert.Equal(await seedings.LaneSeedingsAsync(cycleId, default), await seedings.LaneSeedingsOfCyclesAsync([cycleId], default));
        Assert.Equal(await lots.LaneHarvestsAsync(cycleId, default), await lots.LaneHarvestsOfCyclesAsync([cycleId], default));
        Assert.Equal(await progress.StepsAsync(cycleId, default), await progress.StepsOfCyclesAsync([cycleId], default));
        Assert.Equal(await cycles.EarliestOpenStartOfZoneAsync(zoneId, default), (await cycles.EarliestOpenStartByZoneAsync(default))[zoneId]);

        var raft = (await rafts.GetForReadAsync(raftId, default))!;
        var idle = (await rafts.GetForReadAsync(idleRaftId, default))!;
        var reader = services.GetRequiredService<LaneStateReader>();
        var together = await reader.ReadManyAsync([raft, idle], Today, default);
        foreach (var r in new[] { raft, idle })
        {
            var single = await reader.ReadAsync(r, Today, default);
            Assert.Equal(single.Cycle, together[r.Id].Cycle);
            Assert.Equal(single.States.OrderBy(s => s.Key), together[r.Id].States.OrderBy(s => s.Key));
            Assert.Equal(single.LanesWithRecords.Order(), together[r.Id].LanesWithRecords.Order());
        }

        var header = (await lots.GetHeaderAsync(lotId, default))!;
        var full = (await lots.GetAsync(lotId, default))!;
        Assert.Equal((full.LotCode, full.Raft.ZoneId, full.Raft.Zone.Code), (header.LotCode, header.Raft.ZoneId, header.Raft.Zone.Code));
        var trace = (await lots.GetForTraceAsync(lotId, default))!;
        Assert.Equal(full.Lanes.Select(l => l.Lane.LaneNumber).Order(), trace.Lanes.Select(l => l.Lane.LaneNumber).Order());
        Assert.Equal(full.Cycle.StartDate, trace.Cycle.StartDate);
        Assert.Equal(lotId, (await lots.ListWithLanesForEditAsync([lotId, lotId], default)).Single().Key);
        var ofLanes = await lots.ListOfLanesAsync(cycleId, full.Lanes.Select(l => l.LaneId).ToList(), default);
        Assert.Equal(full.Lanes.Count, ofLanes.Count);
        Assert.All(ofLanes, row => Assert.Equal(lotId, row.Lot.Id));

        var yield = await lots.ListForYieldAsync(null, Today.AddDays(-30), Today, wholeCycles: true, default);
        var yieldRow = Assert.Single(yield);
        Assert.Equal((lotId, full.Raft.Code, full.Raft.Zone.Name, cycleId), (yieldRow.Id, yieldRow.RaftCode, yieldRow.ZoneName, yieldRow.CycleId));

        Assert.Empty(await services.GetRequiredService<IWaterMeasurementRepository>().LatestActiveByZoneAsync([zoneId], default));
        var nursery = await services.GetRequiredService<INurseryBatchRepository>().SearchAsync(
            new NurseryBatchFilter(null, null, null, null, null, null, NurseryBatchSort.Code, true), 1, 20, default);
        Assert.Equal((0, 0m, 0), (nursery.Total, nursery.Totals.QuantityIn, nursery.Totals.ClosedCount));
    }
}
