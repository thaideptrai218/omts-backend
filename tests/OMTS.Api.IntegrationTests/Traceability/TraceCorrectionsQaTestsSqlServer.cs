using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using OMTS.Api.IntegrationTests.Cultivation;
using OMTS.Api.IntegrationTests.Farming;
using OMTS.Api.IntegrationTests.Harvest;
using OMTS.Api.IntegrationTests.Infrastructure;
using OMTS.Application.Features.Files;
using Xunit.Abstractions;
using static OMTS.Api.IntegrationTests.Farming.FarmingTestKit;
using static OMTS.Api.IntegrationTests.Traceability.TraceCorrectionTestKit;

namespace OMTS.Api.IntegrationTests.Traceability;

/// <summary>
/// QA races of TRACE-03 on SQL Server against other writes of the same cycle. Each pair is valid alone; together they would leave a lane of a harvest lot with no seed lot
/// (BR-TRACE-01, MSG-TRACE-03), so at most one may save, nothing answers 500, and a loser leaves nothing behind. The log race checks that no photo file is lost or left over.
/// </summary>
public class TraceCorrectionsQaTestsSqlServer(ITestOutputHelper output)
{
    private const int Rounds = 6;

    private static async Task<(Guid RaftId, Guid CycleId, SeedLotMade SeedLot)> CycleAsync(HttpClient owner, Guid zoneId)
    {
        var raftId = (await owner.CreateRaftAsync(zoneId)).Id();
        var cycleId = (await owner.StartCycleAsync(raftId, new CycleBody(Today.AddDays(-5)))).Id();
        return (raftId, cycleId, await owner.SeedLotAsync(received: Today.AddDays(-5)));
    }

    /// <summary>The answer's status, failing on anything but a save or a refusal (400/409).</summary>
    private async Task<bool> SavedAsync(HttpResponseMessage answer)
    {
        var text = await answer.Content.ReadAsStringAsync();
        Assert.True(answer.StatusCode is HttpStatusCode.OK or HttpStatusCode.Created or HttpStatusCode.BadRequest or HttpStatusCode.Conflict, $"{(int)answer.StatusCode}: {text}");
        // The outcome of each side, so a run shows whether the requests really overlapped.
        output.WriteLine($"{answer.RequestMessage?.Method} {answer.RequestMessage?.RequestUri?.AbsolutePath} -> {(int)answer.StatusCode} {(text.Length > 120 ? text[..120] : text)}");
        return answer.IsSuccessStatusCode;
    }

    /// <summary>BR-TRACE-01: the lanes of the lots of the cycle that no seeding session of the cycle holds.</summary>
    private static Task<List<string>> LotLanesWithoutSeedAsync(FarmingSqlServerHost host, Guid cycleId) => host.QueryAsync(async db =>
    {
        var seeded = await db.SeedingRecordLanes.Where(l => l.SeedingRecord.CycleId == cycleId).Select(l => l.LaneId).Distinct().ToListAsync();
        var missing = await db.HarvestLotLanes.Where(l => l.Lot.CycleId == cycleId && !seeded.Contains(l.LaneId)).Select(l => new { l.Lot.LotCode, l.Lane.LaneNumber }).ToListAsync();
        return missing.Select(m => $"{m.LotCode} lane {m.LaneNumber}").ToList();
    });

    [SqlServerFact]
    public async Task A_session_correction_racing_a_new_harvest_of_the_lane_it_drops_never_leaves_the_lane_without_seed()
    {
        await using var host = await FarmingSqlServerHost.StartAsync();
        var owner = host.Owner.Client;
        var zoneId = (await owner.CreateZoneAsync()).Id();
        for (var round = 0; round < Rounds; round++)
        {
            // Open cycle: lanes 1 and 2 seeded yesterday by one session, lane 1 harvested, so the session is corrected only in TRACE-03.
            var (raftId, cycleId, seedLot) = await CycleAsync(owner, zoneId);
            var session = await owner.SeedingRecordAsync(cycleId, seedLot.Session([1, 2]));
            await owner.HarvestLotAsync(raftId, HarvestFields.OfLanes(1));

            var correction = owner.PutSeedingTraceAsync(session.Id(), session.SeedingCorrection(lanes: [1]));
            var harvest = owner.PostHarvestAsync(raftId, HarvestFields.OfLanes(2));
            await Task.WhenAll(correction, harvest);

            var (corrected, harvested) = (await SavedAsync(await correction), await SavedAsync(await harvest));
            Assert.False(corrected && harvested, $"Round {round}: both saved.");
            Assert.Empty(await LotLanesWithoutSeedAsync(host, cycleId));
        }
    }

    [SqlServerFact]
    public async Task A_lot_correction_adding_a_lane_racing_a_session_correction_dropping_it_never_leaves_the_lane_without_seed()
    {
        await using var host = await FarmingSqlServerHost.StartAsync();
        var owner = host.Owner.Client;
        var zoneId = (await owner.CreateZoneAsync()).Id();
        for (var round = 0; round < Rounds; round++)
        {
            var (raftId, cycleId, seedLot) = await CycleAsync(owner, zoneId);
            var session = await owner.SeedingRecordAsync(cycleId, seedLot.Session([1, 2]));
            var lot = await owner.HarvestLotAsync(raftId, HarvestFields.OfLanes(1));
            await owner.CloseCycleAsync(cycleId);
            var current = await owner.GetSeedingAsync(session.Id());
            var lotVersion = (await owner.GetFromJsonAsync<JsonElement>($"/api/harvest-lots/{lot.Id()}")).Text("rowVersion");

            var sessionCorrection = owner.PutSeedingTraceAsync(session.Id(), current.SeedingCorrection(lanes: [1]));
            var lotCorrection = owner.PutLotTraceAsync(lot.Id(), new HarvestCorrection(HarvestFields.OfLanes(1, 2), Reason, lotVersion));
            await Task.WhenAll(sessionCorrection, lotCorrection);

            var (first, second) = (await SavedAsync(await sessionCorrection), await SavedAsync(await lotCorrection));
            Assert.False(first && second, $"Round {round}: both saved.");
            Assert.Empty(await LotLanesWithoutSeedAsync(host, cycleId));
        }
    }

    [SqlServerFact]
    public async Task Two_session_corrections_each_dropping_the_shared_lane_of_a_lot_never_both_save()
    {
        await using var host = await FarmingSqlServerHost.StartAsync();
        var owner = host.Owner.Client;
        var zoneId = (await owner.CreateZoneAsync()).Id();
        for (var round = 0; round < Rounds; round++)
        {
            // Lane 1 is hung twice (re-hanging, BR-CYCLE-02), then harvested; the cycle is closed.
            var (raftId, cycleId, seedLot) = await CycleAsync(owner, zoneId);
            var a = await owner.SeedingRecordAsync(cycleId, seedLot.Session([1, 2], Today.AddDays(-2)));
            var b = await owner.SeedingRecordAsync(cycleId, seedLot.Session([1, 3], Today.AddDays(-1)));
            await owner.HarvestLotAsync(raftId, HarvestFields.OfLanes(1));
            await owner.CloseCycleAsync(cycleId);
            var (currentA, currentB) = (await owner.GetSeedingAsync(a.Id()), await owner.GetSeedingAsync(b.Id()));

            var dropA = owner.PutSeedingTraceAsync(a.Id(), currentA.SeedingCorrection(lanes: [2]));
            var dropB = owner.PutSeedingTraceAsync(b.Id(), currentB.SeedingCorrection(lanes: [3]));
            await Task.WhenAll(dropA, dropB);

            var (first, second) = (await SavedAsync(await dropA), await SavedAsync(await dropB));
            Assert.False(first && second, $"Round {round}: both saved.");
            Assert.True(first || second, $"Round {round}: neither saved.");
            Assert.Empty(await LotLanesWithoutSeedAsync(host, cycleId));
        }
    }

    [SqlServerFact]
    public async Task Two_log_corrections_at_once_never_answer_500_and_lose_or_leave_no_photo_file()
    {
        await using var host = await FarmingSqlServerHost.StartAsync();
        var owner = host.Owner.Client;
        var zoneId = (await owner.CreateZoneAsync()).Id();
        for (var round = 0; round < Rounds; round++)
        {
            var (_, cycleId, _) = await CycleAsync(owner, zoneId);
            var log = await owner.RecordLogAsync(cycleId, new LogFields { Notes = "before" });
            await owner.CloseCycleAsync(cycleId);
            var kept = log.PhotoKeys();

            var keepAndAdd = owner.PutLogTraceAsync(log.Id(), new LogFields { Notes = "A", KeepPhotoKeys = kept, Photos = [Png], Reason = Reason });
            var replace = owner.PutLogTraceAsync(log.Id(), new LogFields { Notes = "B", KeepPhotoKeys = [], Photos = [Jpeg], Reason = Reason });
            await Task.WhenAll(keepAndAdd, replace);

            var saved = (await SavedAsync(await keepAndAdd) ? 1 : 0) + (await SavedAsync(await replace) ? 1 : 0);
            Assert.True(saved >= 1, $"Round {round}: neither saved.");
            var read = await owner.GetLogAsync(log.Id());
            Assert.Equal(read.Text("notes") == "A" ? 2 : 1, read.PhotoKeys().Count);
            Assert.Equal(saved, await host.QueryAsync(db => db.AuditEvents.CountAsync(e => e.Action == LogAction && e.RecordId == log.Id())));

            // Every photo row has its file, and every file of the category belongs to a photo row.
            var rows = await host.QueryAsync(db => db.CultivationLogPhotos.Select(p => p.FileKey).ToListAsync());
            Assert.Equal(rows.Order(StringComparer.Ordinal), host.StoredKeys(FileCategories.CultivationLogPhotos).Order(StringComparer.Ordinal));
        }
    }
}
