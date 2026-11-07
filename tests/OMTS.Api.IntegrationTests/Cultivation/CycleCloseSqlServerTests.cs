using System.Net;
using Microsoft.EntityFrameworkCore;
using OMTS.Api.IntegrationTests.Farming;
using OMTS.Api.IntegrationTests.Harvest;
using OMTS.Api.IntegrationTests.Infrastructure;
using OMTS.Domain.Constants;
using static OMTS.Api.IntegrationTests.Farming.FarmingTestKit;

namespace OMTS.Api.IntegrationTests.Cultivation;

/// <summary>
/// CYCLE-06 against parallel writes on SQL Server (report §4.3): every write that changes the lanes or the records of a cycle marks the cycle row, and the close saves that row
/// with the version its client read. So a close never saves over a record its summary did not count, a write never enters a cycle closed meanwhile, and nothing answers 500.
/// </summary>
public class CycleCloseSqlServerTests
{
    private static async Task<(FarmingSqlServerHost Host, HttpClient Owner, Guid ZoneId)> StartAsync()
    {
        var host = await FarmingSqlServerHost.StartAsync();
        var owner = host.Owner.Client;
        return (host, owner, (await owner.CreateZoneAsync()).Id());
    }

    /// <summary>A raft with a cycle started five days ago and seed on lanes 1 and 2 yesterday.</summary>
    private static async Task<(Guid RaftId, Guid CycleId, SeedLotMade Lot)> SeededAsync(HttpClient owner, Guid zoneId)
    {
        var raftId = (await owner.CreateRaftAsync(zoneId)).Id();
        var cycleId = (await owner.StartCycleAsync(raftId, new CycleBody(Today.AddDays(-5)))).Id();
        var lot = await owner.SeedLotAsync(received: Today.AddDays(-5));
        await owner.RecordSeedingAsync(cycleId, lot.Session([1, 2]));
        return (raftId, cycleId, lot);
    }

    private static async Task<bool> SucceededAsync(HttpResponseMessage answer, params HttpStatusCode[] success)
    {
        var text = await answer.Content.ReadAsStringAsync();
        Assert.True(success.Contains(answer.StatusCode) || answer.StatusCode == HttpStatusCode.Conflict, $"{(int)answer.StatusCode}: {text}");
        return success.Contains(answer.StatusCode);
    }

    private static Task<int> StatusOfAsync(FarmingSqlServerHost host, Guid cycleId) =>
        host.QueryAsync(db => db.CultivationCycles.Where(c => c.Id == cycleId).Select(c => c.StatusId).SingleAsync());

    [SqlServerFact]
    public async Task A_close_sent_with_the_version_read_before_a_harvest_answers_msg06_and_a_fresh_one_counts_the_lot()
    {
        var (host, owner, zoneId) = await StartAsync();
        await using var _ = host;
        var (raftId, cycleId, _) = await SeededAsync(owner, zoneId);
        var version = (await owner.ClosePreviewAsync(cycleId)).Text("cycleRowVersion");

        await owner.HarvestLotAsync(raftId, HarvestFields.OfLanes(1, 2).FinishedLanes(1, 2));
        var stale = await owner.PostCloseAsync(cycleId, CycleCloseTestKit.Assessment, version);

        Assert.Equal("MSG06", (await stale.ReadAsync(HttpStatusCode.Conflict)).Text("code"));
        Assert.Equal(StatusIds.CultivationCycle.Open, await StatusOfAsync(host, cycleId));

        var closed = await owner.CloseCycleAsync(cycleId);
        Assert.Equal((1, 400m, false), (closed.GetProperty("summary").GetProperty("harvestLotCount").GetInt32(), closed.GetProperty("summary").Number("kgHarvested"), closed.GetProperty("cycle").Flag("closedEarly")));
    }

    [SqlServerFact]
    public async Task A_close_racing_a_harvest_lets_exactly_one_save_and_never_answers_500()
    {
        var (host, owner, zoneId) = await StartAsync();
        await using var _ = host;
        for (var round = 0; round < 8; round++)
        {
            var (raftId, cycleId, _) = await SeededAsync(owner, zoneId);
            var version = (await owner.ClosePreviewAsync(cycleId)).Text("cycleRowVersion");

            var harvest = owner.PostHarvestAsync(raftId, HarvestFields.OfLanes(1));
            var close = owner.PostCloseAsync(cycleId, CycleCloseTestKit.Assessment, version);
            await Task.WhenAll(harvest, close);

            var harvested = await SucceededAsync(await harvest, HttpStatusCode.Created);
            var closed = await SucceededAsync(await close, HttpStatusCode.OK);
            Assert.True(harvested ^ closed, $"Round {round}: harvest {(await harvest).StatusCode}, close {(await close).StatusCode}.");
            Assert.Equal(harvested ? 1 : 0, await host.QueryAsync(db => db.HarvestLots.CountAsync(l => l.CycleId == cycleId)));
            Assert.Equal(closed ? StatusIds.CultivationCycle.Closed : StatusIds.CultivationCycle.Open, await StatusOfAsync(host, cycleId));
        }
    }

    [SqlServerFact]
    public async Task A_close_racing_a_seeding_session_lets_exactly_one_save_and_never_answers_500()
    {
        var (host, owner, zoneId) = await StartAsync();
        await using var _ = host;
        for (var round = 0; round < 8; round++)
        {
            var (_, cycleId, lot) = await SeededAsync(owner, zoneId);
            var version = (await owner.ClosePreviewAsync(cycleId)).Text("cycleRowVersion");

            var seeding = owner.PostSeedingAsync(cycleId, lot.Session([3], Today));
            var close = owner.PostCloseAsync(cycleId, CycleCloseTestKit.Assessment, version);
            await Task.WhenAll(seeding, close);

            var seeded = await SucceededAsync(await seeding, HttpStatusCode.Created);
            var closed = await SucceededAsync(await close, HttpStatusCode.OK);
            Assert.True(seeded ^ closed, $"Round {round}: seeding {(await seeding).StatusCode}, close {(await close).StatusCode}.");
            Assert.Equal(seeded ? 2 : 1, await host.QueryAsync(db => db.SeedingRecords.CountAsync(s => s.CycleId == cycleId)));
        }
    }

    [SqlServerFact]
    public async Task Parallel_closes_of_one_cycle_save_once_with_one_audit_row()
    {
        var (host, owner, zoneId) = await StartAsync();
        await using var _ = host;
        var (_, cycleId, _) = await SeededAsync(owner, zoneId);
        var version = (await owner.ClosePreviewAsync(cycleId)).Text("cycleRowVersion");

        var answers = await Task.WhenAll(Enumerable.Range(0, 4).Select(i => owner.PostCloseAsync(cycleId, $"{CycleCloseTestKit.Assessment} {i}", version)));

        var saved = 0;
        foreach (var answer in answers)
        {
            saved += await SucceededAsync(answer, HttpStatusCode.OK) ? 1 : 0;
        }

        Assert.Equal(1, saved);
        Assert.Equal(1, await host.QueryAsync(db => db.AuditEvents.CountAsync(e => e.Action == CycleCloseTestKit.ClosedAction && e.RecordId == cycleId)));
        Assert.Equal(StatusIds.CultivationCycle.Closed, await StatusOfAsync(host, cycleId));
    }
}
