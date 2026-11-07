using System.Net;
using OMTS.Api.IntegrationTests.Cultivation;
using OMTS.Api.IntegrationTests.Farming;
using OMTS.Api.IntegrationTests.Harvest;
using OMTS.Api.IntegrationTests.Infrastructure;
using OMTS.Domain.Enums;
using static OMTS.Api.IntegrationTests.Farming.FarmingTestKit;

namespace OMTS.Api.IntegrationTests.Traceability;

/// <summary>
/// TRACE-03 on a Closed cycle, after a real close (CYCLE-06): its sessions, logs and harvest data are corrected only here (BR-CYCLE-04, BR-TRACE-04), and the lane states and the
/// cycle summary are worked out from the corrected records (TRACE-03 POST-3, BR-CYCLE-06).
/// </summary>
public class TraceClosedCycleCorrectionTests(AuthApiFactory factory) : IClassFixture<AuthApiFactory>
{
    /// <summary>The traced raft with a log of today, closed early today by the Owner.</summary>
    private async Task<(TracedRaft Raft, Guid LogId)> ClosedAsync()
    {
        var t = await factory.TracedRaftAsync();
        var log = await t.Owner.Client.RecordLogAsync(t.CycleId, new LogFields { Notes = "Buoys fine" });
        await t.Owner.Client.CloseCycleAsync(t.CycleId);
        return (t, log.Id());
    }

    [Fact]
    public async Task A_session_of_a_closed_cycle_is_corrected_and_the_summary_and_lane_states_follow()
    {
        var (t, _) = await ClosedAsync();
        var atClose = await t.Owner.Client.CycleSummaryAsync(t.CycleId);
        Assert.Equal(10m, atClose.Number("seedHung"));
        Assert.Equal([1, 2, 3, 4, 5], atClose.Numbers("notHarvestedLanes"));
        Assert.Equal("Growing", await t.Owner.Client.LaneStateAsync(t.RaftId, t.CycleId, 5));

        // Lanes 4 and 5 are in no lot: taking them out changes no origin information.
        var preview = await (await t.Manager.Client.PreviewSeedingAsync(t.SessionId, t.Session.SeedingCorrection(lanes: [1, 2, 3], quantity: 6m))).ReadAsync(HttpStatusCode.OK);
        Assert.Equal(["lanes", "quantity"], preview.Strings("changedFields"));
        Assert.Empty(preview.GetProperty("affectedLots").EnumerateArray());
        await (await t.Manager.Client.PutSeedingTraceAsync(t.SessionId, t.Session.SeedingCorrection(lanes: [1, 2, 3], quantity: 6m))).AgreementRefusalAsync();
        await (await t.Manager.Client.PutSeedingTraceAsync(t.SessionId, t.Session.SeedingCorrection(lanes: [1, 2, 3], quantity: 6m, ownerApproved: true))).ReadAsync(HttpStatusCode.OK);

        var summary = await t.Owner.Client.CycleSummaryAsync(t.CycleId);
        Assert.Equal(6m, summary.Number("seedHung"));
        Assert.Equal([1, 2, 3], summary.Numbers("notHarvestedLanes"));
        Assert.Equal(("NotSeeded", "Growing"), (await t.Owner.Client.LaneStateAsync(t.RaftId, t.CycleId, 5), await t.Owner.Client.LaneStateAsync(t.RaftId, t.CycleId, 2)));

        // The cultivation screen stays locked after the close (BR-CYCLE-04).
        Assert.Equal("MSG-GEN-09", (await (await t.Owner.Client.PutSeedingAsync(t.SessionId, t.Session.AsCorrection())).ReadAsync(HttpStatusCode.Conflict)).Text("code"));
    }

    [Fact]
    public async Task The_harvest_data_of_a_lot_of_a_closed_cycle_is_corrected_but_never_its_harvest_time()
    {
        var (t, _) = await ClosedAsync();
        await factory.PublishLotAsync(t.LotId);
        var before = await factory.LotRowAsync(t.LotId);
        var fields = HarvestFields.OfLanes(1, 2).FinishedLanes(1, 2) with { Cages = "10" };

        var preview = await (await t.Manager.Client.PreviewLotAsync(t.LotId, new { laneNumbers = new[] { 1, 2 }, finishedLaneNumbers = new[] { 1, 2 }, cageCount = 10, reason = TraceCorrectionTestKit.Reason }))
            .ReadAsync(HttpStatusCode.OK);
        Assert.Equal(["lanes", "finishedLanes", "cageCount", "estimatedKg"], preview.Strings("changedFields"));
        Assert.Equal(preview.GetProperty("before").Text("harvestedAt"), preview.GetProperty("after").Text("harvestedAt"));
        Assert.Equal([t.LotCode], preview.Strings("affectedPublishedLotCodes"));

        var refused = await (await factory.SendLotTraceAsync(t.Manager.Client, t.LotId, fields)).AgreementRefusalAsync();
        Assert.Equal([t.LotCode], refused.Strings("lotCodes"));
        var saved = await (await factory.SendLotTraceAsync(t.Manager.Client, t.LotId, fields, ownerApproved: true)).ReadAsync(HttpStatusCode.OK);

        var lot = saved.GetProperty("lot");
        Assert.Equal([1, 2], lot.Numbers("laneNumbers"));
        Assert.Equal([1, 2], lot.Numbers("finishedLaneNumbers"));
        Assert.Equal(10, lot.GetProperty("cageCount").GetInt32());
        // BR-HARVEST-02: worked out again with the kg per cage copied at record time.
        Assert.Equal(10 * before.KgPerCage, lot.Kg("estimatedKg"));
        Assert.Equal([t.LotCode], saved.Strings("affectedPublishedLotCodes"));
        var after = await factory.LotRowAsync(t.LotId);
        Assert.Equal((before.LotCode, before.HarvestedAt, before.HarvestDate, before.KgPerCage, before.PublishedAt, HarvestLotOriginState.Published),
            (after.LotCode, after.HarvestedAt, after.HarvestDate, after.KgPerCage, after.PublishedAt, after.OriginState));
        var row = Assert.Single(await factory.AuditRowsAsync(TraceCorrectionTestKit.LotAction, t.LotId));
        Assert.Contains("\"OwnerApprovalConfirmed\":true", row.After);
        Assert.Contains($"\"AffectedPublishedLotCodes\":[\"{t.LotCode}\"]", row.After);
        Assert.Equal(TraceCorrectionTestKit.Reason, row.Reason);

        // The summary and the lane states follow (POST-3); a Closed cycle sends no reminder to close it.
        var summary = await t.Owner.Client.CycleSummaryAsync(t.CycleId);
        Assert.Equal(10 * before.KgPerCage, summary.Number("kgHarvested"));
        Assert.Equal("Harvested", await t.Owner.Client.LaneStateAsync(t.RaftId, t.CycleId, 2));
        Assert.Empty(await factory.NoticesAsync("CYCLE_CLOSE_REMINDER", t.CycleId));

        // HARVEST-01.1 stays locked after the close.
        Assert.Equal("MSG-GEN-09", await (await factory.SendCorrectionAsync(t.Owner.Client, t.LotId, fields)).ErrorCodeAsync());
    }

    [Fact]
    public async Task A_lane_of_a_closed_lot_follows_the_harvest_rules()
    {
        var (t, _) = await ClosedAsync();

        // Lane 3 had seed by the harvest date, so it may be added; a finished lane must be one of the lot's lanes; the reason is required.
        Assert.Equal(["Finished lanes", "Reason"],
            await (await factory.SendLotTraceAsync(t.Owner.Client, t.LotId, HarvestFields.OfLanes(1).FinishedLanes(3), reason: null)).InvalidFieldsAsync());
        Assert.Equal(["Lanes"], await (await factory.SendLotTraceAsync(t.Owner.Client, t.LotId, HarvestFields.OfLanes(1, 9))).InvalidFieldsAsync());
        var saved = await (await factory.SendLotTraceAsync(t.Owner.Client, t.LotId, HarvestFields.OfLanes(1, 3))).ReadAsync(HttpStatusCode.OK);
        Assert.Equal([1, 3], saved.GetProperty("lot").Numbers("laneNumbers"));
        Assert.Contains("\"OwnerApprovalConfirmed\":false", Assert.Single(await factory.AuditRowsAsync(TraceCorrectionTestKit.LotAction, t.LotId)).After);
    }

    [Fact]
    public async Task A_log_of_a_closed_cycle_is_corrected_after_the_owners_agreement_and_names_no_lot()
    {
        var (t, logId) = await ClosedAsync();
        var keys = (await t.Owner.Client.GetLogAsync(logId)).PhotoKeys();

        var preview = await (await t.Manager.Client.PreviewLogAsync(logId)).ReadAsync(HttpStatusCode.OK);
        Assert.Equal(("CultivationLog", "Buoys fine", true), (preview.Text("recordType"), preview.GetProperty("before").Text("notes"), preview.Flag("needsOwnerApproval")));
        Assert.Equal(("MSG-TRACE-04", "Has the Cooperative Owner agreed in person that the data is wrong?"), (preview.Text("confirmMessageCode"), preview.Text("confirmMessage")));
        Assert.Empty(preview.GetProperty("affectedLots").EnumerateArray());

        var fields = new LogFields { Notes = "Two buoys replaced", KeepPhotoKeys = keys, Photos = [], Reason = TraceCorrectionTestKit.Reason };
        var refused = await (await t.Manager.Client.PutLogTraceAsync(logId, fields)).AgreementRefusalAsync();
        Assert.Empty(refused.Strings("lotCodes"));
        Assert.Equal("Buoys fine", (await t.Owner.Client.GetLogAsync(logId)).Text("notes"));

        var saved = await (await t.Manager.Client.PutLogTraceAsync(logId, fields, ownerApproved: true)).ReadAsync(HttpStatusCode.OK);
        Assert.Equal("Two buoys replaced", saved.GetProperty("log").Text("notes"));
        Assert.Equal(keys, saved.GetProperty("log").PhotoKeys());
        Assert.Equal("Cultivation log saved.", saved.Text("message"));
        var row = Assert.Single(await factory.AuditRowsAsync(TraceCorrectionTestKit.LogAction, logId));
        Assert.Contains("\"Notes\":\"Two buoys replaced\"", row.After);
        Assert.Contains("\"OwnerApprovalConfirmed\":true", row.After);

        // The CYCLE-04 rules: at least one photo, a visit within the cycle, a reason.
        Assert.Equal(["Visit date", "Photos", "Reason"],
            await (await t.Owner.Client.PutLogTraceAsync(logId, new LogFields { VisitDate = Today.AddDays(1), Photos = [], Reason = null })).InvalidFieldsAsync());
        // CYCLE-04.1 stays locked after the close.
        Assert.Equal(HttpStatusCode.Conflict, (await t.Owner.Client.PutLogAsync(logId, fields)).StatusCode);
    }

    [Fact]
    public async Task Logs_and_lots_of_an_open_cycle_are_corrected_in_their_own_screens_not_here()
    {
        var t = await factory.TracedRaftAsync();
        var log = await t.Owner.Client.RecordLogAsync(t.CycleId);

        foreach (var answer in new[]
        {
            await t.Owner.Client.PreviewLogAsync(log.Id()),
            await t.Owner.Client.PutLogTraceAsync(log.Id(), new LogFields { KeepPhotoKeys = log.PhotoKeys(), Photos = [], Reason = TraceCorrectionTestKit.Reason }),
            await t.Owner.Client.PreviewLotAsync(t.LotId, new { laneNumbers = new[] { 1 }, finishedLaneNumbers = Array.Empty<int>(), cageCount = 9, reason = TraceCorrectionTestKit.Reason }),
            await factory.SendLotTraceAsync(t.Owner.Client, t.LotId, HarvestFields.OfLanes(1) with { Cages = "9" }),
        })
        {
            var refused = await answer.ReadAsync(HttpStatusCode.Conflict);
            Assert.Equal(("MSG-GEN-09", $"The cultivation cycle of raft {(await t.Owner.Client.GetHarvestLotAsync(t.LotId)).Text("raftCode")} is Open and cannot be used for this action."),
                (refused.Text("code"), refused.Text("detail")));
        }

        Assert.Equal(8, (await factory.LotRowAsync(t.LotId)).CageCount);
    }
}
