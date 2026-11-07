using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using OMTS.Api.IntegrationTests.Cultivation;
using OMTS.Api.IntegrationTests.Farming;
using OMTS.Api.IntegrationTests.Harvest;
using OMTS.Api.IntegrationTests.Infrastructure;
using OMTS.Domain.Constants;
using static OMTS.Api.IntegrationTests.Farming.FarmingTestKit;

namespace OMTS.Api.IntegrationTests.Traceability;

/// <summary>
/// TRACE-03 against parallel writes on SQL Server (report §4.3): a correction saves the record with the version its client read and marks the cycle row, so of two corrections of one
/// record exactly one saves, a correction racing a close never saves over a cycle it did not read, and nothing answers 500.
/// </summary>
public class TraceCorrectionSqlServerTests
{
    /// <summary>A raft with a cycle started five days ago, seed on lanes 1 and 2 yesterday and lane 1 harvested today.</summary>
    private static async Task<(Guid RaftId, Guid CycleId, JsonElement Session, JsonElement Lot)> TracedAsync(HttpClient owner, Guid zoneId)
    {
        var raftId = (await owner.CreateRaftAsync(zoneId)).Id();
        var cycleId = (await owner.StartCycleAsync(raftId, new CycleBody(Today.AddDays(-5)))).Id();
        var seedLot = await owner.SeedLotAsync(received: Today.AddDays(-5));
        var session = await owner.SeedingRecordAsync(cycleId, seedLot.Session([1, 2]));
        var lot = await owner.HarvestLotAsync(raftId, HarvestFields.OfLanes(1));
        return (raftId, cycleId, session, lot);
    }

    private static async Task<bool> SucceededAsync(HttpResponseMessage answer)
    {
        var text = await answer.Content.ReadAsStringAsync();
        Assert.True(answer.StatusCode is HttpStatusCode.OK or HttpStatusCode.Conflict, $"{(int)answer.StatusCode}: {text}");
        return answer.StatusCode == HttpStatusCode.OK;
    }

    private static Task<int> AuditCountAsync(FarmingSqlServerHost host, string action, Guid recordId) =>
        host.QueryAsync(db => db.AuditEvents.CountAsync(e => e.Action == action && e.RecordId == recordId));

    [SqlServerFact]
    public async Task Two_corrections_of_one_session_with_the_same_version_save_once()
    {
        await using var host = await FarmingSqlServerHost.StartAsync();
        var owner = host.Owner.Client;
        var zoneId = (await owner.CreateZoneAsync()).Id();
        for (var round = 0; round < 6; round++)
        {
            var (_, _, session, _) = await TracedAsync(owner, zoneId);

            var answers = await Task.WhenAll(
                owner.PutSeedingTraceAsync(session.Id(), session.SeedingCorrection(quantity: 11m)),
                owner.PutSeedingTraceAsync(session.Id(), session.SeedingCorrection(quantity: 12m)));

            var saved = 0;
            foreach (var answer in answers)
            {
                saved += await SucceededAsync(answer) ? 1 : 0;
            }

            Assert.Equal(1, saved);
            Assert.Equal(1, await AuditCountAsync(host, TraceCorrectionTestKit.SeedingAction, session.Id()));
        }
    }

    [SqlServerFact]
    public async Task A_correction_racing_the_close_of_its_cycle_never_answers_500_and_a_loser_saves_nothing()
    {
        await using var host = await FarmingSqlServerHost.StartAsync();
        var owner = host.Owner.Client;
        var zoneId = (await owner.CreateZoneAsync()).Id();
        for (var round = 0; round < 8; round++)
        {
            var (_, cycleId, session, _) = await TracedAsync(owner, zoneId);
            var version = (await owner.ClosePreviewAsync(cycleId)).Text("cycleRowVersion");

            var correction = owner.PutSeedingTraceAsync(session.Id(), session.SeedingCorrection(quantity: 15m));
            var close = owner.PostCloseAsync(cycleId, CycleCloseTestKit.Assessment, version);
            await Task.WhenAll(correction, close);

            var corrected = await SucceededAsync(await correction);
            var closed = await SucceededAsync(await close);
            Assert.True(corrected || closed, $"Round {round}: neither saved.");
            Assert.Equal(corrected ? 1 : 0, await AuditCountAsync(host, TraceCorrectionTestKit.SeedingAction, session.Id()));
            Assert.Equal(corrected ? 15m : 10m, await host.QueryAsync(db => db.SeedingRecords.Where(s => s.Id == session.Id()).Select(s => s.Quantity).SingleAsync()));
            Assert.Equal(closed ? StatusIds.CultivationCycle.Closed : StatusIds.CultivationCycle.Open,
                await host.QueryAsync(db => db.CultivationCycles.Where(c => c.Id == cycleId).Select(c => c.StatusId).SingleAsync()));
            if (corrected && closed)
            {
                // Both can save only one after the other: the correction then read the Closed cycle, which TRACE-03 still corrects.
                var closedAt = await host.QueryAsync(db => db.AuditEvents.Where(e => e.Action == CycleCloseTestKit.ClosedAction && e.RecordId == cycleId).Select(e => e.OccurredAt).SingleAsync());
                var correctedAt = await host.QueryAsync(db => db.AuditEvents.Where(e => e.Action == TraceCorrectionTestKit.SeedingAction && e.RecordId == session.Id()).Select(e => e.OccurredAt).SingleAsync());
                Assert.True(correctedAt >= closedAt, $"Round {round}: corrected at {correctedAt:O}, closed at {closedAt:O}.");
            }
        }
    }

    [SqlServerFact]
    public async Task Two_corrections_of_one_lot_of_a_closed_cycle_with_the_same_version_save_once()
    {
        await using var host = await FarmingSqlServerHost.StartAsync();
        var owner = host.Owner.Client;
        var zoneId = (await owner.CreateZoneAsync()).Id();
        for (var round = 0; round < 6; round++)
        {
            var (_, cycleId, _, lot) = await TracedAsync(owner, zoneId);
            await owner.CloseCycleAsync(cycleId);
            var version = (await owner.GetFromJsonAsync<JsonElement>($"/api/harvest-lots/{lot.Id()}")).Text("rowVersion");

            var answers = await Task.WhenAll(
                owner.PutLotTraceAsync(lot.Id(), new HarvestCorrection(HarvestFields.OfLanes(1) with { Cages = "9" }, TraceCorrectionTestKit.Reason, version)),
                owner.PutLotTraceAsync(lot.Id(), new HarvestCorrection(HarvestFields.OfLanes(1, 2) with { Cages = "10" }, TraceCorrectionTestKit.Reason, version)));

            var saved = 0;
            foreach (var answer in answers)
            {
                saved += await SucceededAsync(answer) ? 1 : 0;
            }

            Assert.Equal(1, saved);
            Assert.Equal(1, await AuditCountAsync(host, TraceCorrectionTestKit.LotAction, lot.Id()));
        }
    }
}
