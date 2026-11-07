using System.Net;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using OMTS.Api.IntegrationTests.Cultivation;
using OMTS.Api.IntegrationTests.Farming;
using OMTS.Api.IntegrationTests.Infrastructure;
using OMTS.Application.Features.Files;
using OMTS.Domain.Entities.Harvest;
using static OMTS.Api.IntegrationTests.Farming.FarmingTestKit;

namespace OMTS.Api.IntegrationTests.Harvest;

/// <summary>HARVEST-01 on SQL Server: real row versions, the constraints, and parallel saves. Every race answers 2xx or 409, never a 500.</summary>
public class HarvestLotSqlServerTests
{
    private const int Rounds = 4;

    private static async Task<(FarmingSqlServerHost Host, HttpClient Owner, Guid ZoneId)> StartAsync()
    {
        var host = await FarmingSqlServerHost.StartAsync();
        return (host, host.Owner.Client, (await host.Owner.Client.CreateZoneAsync()).Id());
    }

    /// <summary>A new raft of the zone with seed on every lane in its Open cycle, so each round of a race starts clean.</summary>
    private static async Task<(Guid RaftId, Guid CycleId, SeedLotMade Lot, JsonElement Session)> SeededAsync(HttpClient owner, Guid zoneId, string? raftCode = null)
    {
        var raftId = (await owner.CreateRaftAsync(zoneId, NewRaftBody(raftCode))).Id();
        var cycleId = (await owner.StartCycleAsync(raftId, new CycleBody(Today.AddDays(-10)))).Id();
        var lot = await owner.SeedLotAsync();
        return (raftId, cycleId, lot, await owner.SeedingRecordAsync(cycleId, lot.Session([1, 2, 3, 4, 5])));
    }

    private static async Task AssertAnsweredAsync(IEnumerable<HttpResponseMessage> answers)
    {
        foreach (var answer in answers)
        {
            var text = await answer.Content.ReadAsStringAsync();
            Assert.True(answer.IsSuccessStatusCode || answer.StatusCode == HttpStatusCode.Conflict, $"{(int)answer.StatusCode}: {text}");
        }
    }

    [SqlServerFact]
    public async Task A_lot_moves_the_version_of_the_cycle_and_an_open_cycle_form_or_mark_is_stale()
    {
        var (host, owner, zoneId) = await StartAsync();
        await using var _ = host;
        var raft = await SeededAsync(owner, zoneId);
        var list = await owner.SeedingListAsync(raft.CycleId);

        var saved = await owner.RecordHarvestAsync(raft.RaftId, HarvestFields.OfLanes(1));

        Assert.Equal(8, Convert.FromBase64String(saved.GetProperty("lot").Text("rowVersion")).Length);
        Assert.NotEqual(list.Text("cycleRowVersion"), saved.Text("cycleRowVersion"));
        Assert.Equal("MSG06", await (await owner.PostSeedingCompleteAsync(raft.CycleId, list.Text("cycleRowVersion"))).ErrorCodeAsync());
        Assert.Equal(saved.Text("cycleRowVersion"), (await owner.SeedingListAsync(raft.CycleId)).Text("cycleRowVersion"));
    }

    // Parallel records on one raft on one day: each answers 2xx or 409, codes are unique, and every saved lot told its Owner once.
    [SqlServerFact]
    public async Task Parallel_records_on_one_raft_and_one_day_answer_2xx_or_409_and_every_saved_lot_has_its_own_code()
    {
        var (host, owner, zoneId) = await StartAsync();
        await using var _ = host;
        var manager = await host.StaffInZoneAsync(zoneId);
        for (var round = 0; round < Rounds; round++)
        {
            var raft = await SeededAsync(owner, zoneId);
            var answers = await Task.WhenAll(Enumerable.Range(0, 5).Select(_ => manager.Client.PostHarvestAsync(raft.RaftId, new HarvestFields { Photos = [Jpeg] })));

            await AssertAnsweredAsync(answers);
            var saved = answers.Count(a => a.StatusCode == HttpStatusCode.Created);
            Assert.True(saved >= 1, "One of the parallel records must be saved.");

            var codes = await host.QueryAsync(db => db.HarvestLots.Where(l => l.RaftId == raft.RaftId).OrderBy(l => l.CreatedAt).Select(l => l.LotCode).ToListAsync());
            Assert.Equal(saved, codes.Count);
            Assert.Equal(saved, codes.Distinct().Count());
            var ids = await host.QueryAsync(db => db.HarvestLots.Where(l => l.RaftId == raft.RaftId).Select(l => l.Id).ToListAsync());
            Assert.Equal(saved, await host.QueryAsync(db => db.Notifications.CountAsync(n => n.Type == "HARVEST_LOT_RECORDED" && ids.Contains(n.RelatedRecordId!.Value))));

            // A new send after the race is saved with the next code, and no file is left of a refused save.
            var after = await manager.Client.HarvestLotAsync(raft.RaftId);
            Assert.DoesNotContain(after.Text("lotCode"), codes);
            var inDatabase = await host.QueryAsync(db => db.HarvestLotPhotos.Select(p => p.FileKey).OrderBy(k => k).ToListAsync());
            Assert.Equal(inDatabase, host.StoredKeys(FileCategories.HarvestLotPhotos));
        }
    }

    // Rafts B01 and KV01-B01 of one zone share no cycle, only a code: the unique index decides, and the loser takes the next code with no error to anybody.
    [SqlServerFact]
    public async Task Two_rafts_that_race_for_one_code_are_both_saved_with_different_codes()
    {
        var (host, owner, _) = await StartAsync();
        await using var _host = host;
        var zoneId = (await owner.CreateZoneAsync(NewZoneBody("KV77"))).Id();
        for (var round = 0; round < Rounds * 2; round++)
        {
            var (plain, prefixed) = ($"R{round}", $"KV77-R{round}");
            var first = await SeededAsync(owner, zoneId, plain);
            var second = await SeededAsync(owner, zoneId, prefixed);

            var answers = await Task.WhenAll(owner.PostHarvestAsync(first.RaftId, new HarvestFields { Photos = [Jpeg] }), owner.PostHarvestAsync(second.RaftId, new HarvestFields { Photos = [Png] }));

            await AssertAnsweredAsync(answers);
            Assert.All(answers, a => Assert.Equal(HttpStatusCode.Created, a.StatusCode));
            var codes = await host.QueryAsync(db => db.HarvestLots.Where(l => l.RaftId == first.RaftId || l.RaftId == second.RaftId).Select(l => l.LotCode).ToListAsync());
            Assert.Equal(2, codes.Distinct().Count());
        }

        Assert.Equal(await host.QueryAsync(db => db.HarvestLotPhotos.CountAsync()), host.StoredKeys(FileCategories.HarvestLotPhotos).Count);
    }

    // Two trips finishing the last two lanes in one moment: whichever is saved second sees the other, so the reminder is never lost, nor sent twice.
    [SqlServerFact]
    public async Task Parallel_lots_finishing_the_last_lanes_send_the_reminder_once_when_both_are_saved()
    {
        var (host, owner, zoneId) = await StartAsync();
        await using var _ = host;
        var manager = await host.StaffInZoneAsync(zoneId);
        for (var round = 0; round < Rounds; round++)
        {
            var raft = await SeededAsync(owner, zoneId);
            await manager.Client.HarvestLotAsync(raft.RaftId, HarvestFields.OfLanes(1, 2, 3).FinishedLanes(1, 2, 3));

            var answers = await Task.WhenAll(
                manager.Client.PostHarvestAsync(raft.RaftId, HarvestFields.OfLanes(4).FinishedLanes(4)),
                manager.Client.PostHarvestAsync(raft.RaftId, HarvestFields.OfLanes(5).FinishedLanes(5)));

            await AssertAnsweredAsync(answers);
            foreach (var (answer, lane) in answers.Zip(new[] { 4, 5 }))
            {
                if (answer.StatusCode != HttpStatusCode.Created)
                {
                    await manager.Client.HarvestLotAsync(raft.RaftId, HarvestFields.OfLanes(lane).FinishedLanes(lane));
                }
            }

            Assert.Equal(1, await host.QueryAsync(db => db.Notifications.CountAsync(n => n.Type == "CYCLE_CLOSE_REMINDER" && n.RelatedRecordId == raft.CycleId && n.RecipientAccountId == manager.Account.Id)));
        }
    }

    // The removal and the lot read the lanes before each other saved: the cycle row makes one of them fail, also when the lot reads the states just before the removal commits.
    [SqlServerFact]
    public async Task A_lot_against_the_removal_of_its_session_answers_2xx_or_409_and_never_stands_on_a_lane_with_no_seed()
    {
        var (host, owner, zoneId) = await StartAsync();
        await using var _ = host;
        for (var round = 0; round < Rounds; round++)
        {
            var raft = await SeededAsync(owner, zoneId);
            var harvest = owner.PostHarvestAsync(raft.RaftId, HarvestFields.OfLanes(1));
            var remove = owner.RemoveSeedingAsync(raft.Session.Id(), rowVersion: raft.Session.Text("rowVersion"));

            var answers = await Task.WhenAll(harvest, remove);

            var harvestAnswer = answers[0];
            Assert.True(harvestAnswer.StatusCode is HttpStatusCode.Created or HttpStatusCode.Conflict or HttpStatusCode.BadRequest, $"{harvestAnswer.StatusCode}: {await harvestAnswer.Content.ReadAsStringAsync()}");
            await AssertAnsweredAsync([answers[1]]);
            var lots = await host.QueryAsync(db => db.HarvestLots.CountAsync(l => l.RaftId == raft.RaftId));
            var sessions = await host.QueryAsync(db => db.SeedingRecords.CountAsync(r => r.CycleId == raft.CycleId));
            Assert.False(lots == 1 && sessions == 0, "A lot must not stand on a lane whose only session was removed.");
        }
    }

    // BR-HARVEST-02: the lot copies the setting of the moment, one or the other, and the two saves do not get in each other's way.
    [SqlServerFact]
    public async Task Parallel_settings_and_records_never_fail_and_each_lot_holds_a_value_that_was_set()
    {
        var (host, owner, zoneId) = await StartAsync();
        await using var _ = host;
        for (var round = 0; round < Rounds; round++)
        {
            var raft = await SeededAsync(owner, zoneId);
            var current = await owner.HarvestSettingsAsync();
            var settings = owner.PutHarvestSettingsAsync(50m + round + 1, 3, current.Text("rowVersion"));
            var lot = owner.PostHarvestAsync(raft.RaftId);

            await AssertAnsweredAsync(await Task.WhenAll(settings, lot));

            var saved = await host.QueryAsync(db => db.HarvestLots.Where(l => l.RaftId == raft.RaftId).Select(l => l.KgPerCage).SingleAsync());
            Assert.Contains(saved, new[] { 50m + round, 50m + round + 1 });
        }
    }
}
