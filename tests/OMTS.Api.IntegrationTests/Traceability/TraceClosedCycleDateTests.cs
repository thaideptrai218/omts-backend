using System.Net;
using OMTS.Api.IntegrationTests.Cultivation;
using OMTS.Api.IntegrationTests.Farming;
using OMTS.Api.IntegrationTests.Harvest;
using static OMTS.Api.IntegrationTests.Harvest.HarvestLotCorrectionTestKit;

namespace OMTS.Api.IntegrationTests.Traceability;

/// <summary>
/// TRACE-03 on the days the records name: a corrected session or log stays within its cycle, from the start to the close date (BR-CYCLE-02, BR-CYCLE-05), and a session is not moved
/// onto a day its lane was already in a lot (BR-CYCLE-02). The clock is set by each step.
/// </summary>
public class TraceClosedCycleDateTests
{
    [Fact]
    public async Task A_corrected_session_or_log_stays_between_the_start_and_the_close_date_of_its_cycle()
    {
        using var api = new CyclesQaClockApiFactory();
        var (owner, _, raftId, cycleId) = await api.ClockedRaftAsync();
        var session = (await owner.SeedingListAsync(cycleId)).GetProperty("records")[0];
        api.Clock.At = InVietnam(10, 20);
        await owner.HarvestLotAsync(raftId, HarvestFields.OfLanes(1));
        var log = await owner.RecordLogAsync(cycleId, new LogFields { VisitDate = new DateOnly(2026, 10, 20) });
        api.Clock.At = InVietnam(10, 25);
        await owner.CloseCycleAsync(cycleId);
        api.Clock.At = InVietnam(10, 30);

        Assert.Equal(["Seeding date"], await (await owner.PutSeedingTraceAsync(session.Id(), session.SeedingCorrection(date: new DateOnly(2026, 10, 27)))).InvalidFieldsAsync());
        var harvested = await (await owner.PutSeedingTraceAsync(session.Id(), session.SeedingCorrection(date: new DateOnly(2026, 10, 21)))).ReadAsync(HttpStatusCode.BadRequest);
        Assert.Equal("Lanes 1 cannot be seeded on 21/10/2026.", harvested.Text("hint"));
        var saved = await (await owner.PutSeedingTraceAsync(session.Id(), session.SeedingCorrection(date: new DateOnly(2026, 10, 5)))).ReadAsync(HttpStatusCode.OK);
        Assert.Equal(new DateOnly(2026, 10, 5), saved.GetProperty("record").Date("seedingDate"));
        Assert.Equal("Growing", await owner.LaneStateAsync(raftId, cycleId, 2));

        var keep = log.PhotoKeys();
        Assert.Equal(["Visit date"], await (await owner.PutLogTraceAsync(log.Id(),
            new LogFields { VisitDate = new DateOnly(2026, 10, 27), KeepPhotoKeys = keep, Photos = [], Reason = TraceCorrectionTestKit.Reason })).InvalidFieldsAsync());
        var corrected = await (await owner.PutLogTraceAsync(log.Id(),
            new LogFields { VisitDate = new DateOnly(2026, 10, 24), KeepPhotoKeys = keep, Photos = [], Reason = TraceCorrectionTestKit.Reason })).ReadAsync(HttpStatusCode.OK);
        Assert.Equal(new DateOnly(2026, 10, 24), corrected.GetProperty("log").Date("visitDate"));
    }
}
