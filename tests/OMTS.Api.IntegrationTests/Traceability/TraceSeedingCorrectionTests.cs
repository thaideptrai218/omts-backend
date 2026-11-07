using System.Net;
using System.Text.Json;
using OMTS.Api.IntegrationTests.Cultivation;
using OMTS.Api.IntegrationTests.Farming;
using OMTS.Api.IntegrationTests.Harvest;
using OMTS.Api.IntegrationTests.Infrastructure;
using OMTS.Domain.Enums;
using static OMTS.Api.IntegrationTests.Farming.FarmingTestKit;

namespace OMTS.Api.IntegrationTests.Traceability;

/// <summary>TRACE-03.0 on an Open cycle: a seeding session with a lane in a harvest lot, which CYCLE-02.2 no longer allows (BR-CYCLE-02, BR-TRACE-04, BR-TRACE-05).</summary>
public class TraceSeedingCorrectionTests(AuthApiFactory factory) : IClassFixture<AuthApiFactory>
{
    private static string Agreement(string lotCode) =>
        $"This correction changes the origin information of harvest lots {lotCode}. Has the Cooperative Owner agreed in person that the data is wrong?";

    [Fact]
    public async Task A_zone_manager_corrects_a_session_in_a_lot_only_after_confirming_the_owners_agreement()
    {
        var t = await factory.TracedRaftAsync();
        var earlier = Today.AddDays(-2);

        // Steps 5 and 6: the session the Owner recorded, its new date and the lot whose origin information changes.
        var preview = await (await t.Manager.Client.PreviewSeedingAsync(t.SessionId, t.Session.SeedingCorrection(date: earlier))).ReadAsync(HttpStatusCode.OK);
        Assert.Equal(("SeedingRecord", true, "MSG-TRACE-04", Agreement(t.LotCode)),
            (preview.Text("recordType"), preview.Flag("needsOwnerApproval"), preview.Text("confirmMessageCode"), preview.Text("confirmMessage")));
        Assert.Equal(["seedingDate"], preview.Strings("changedFields"));
        Assert.Equal((Today.AddDays(-1), earlier), (preview.GetProperty("before").Date("seedingDate"), preview.GetProperty("after").Date("seedingDate")));
        var affected = Assert.Single(preview.GetProperty("affectedLots").EnumerateArray());
        Assert.Equal((t.LotCode, "NotPublished"), (affected.Text("lotCode"), affected.Text("originState")));
        Assert.Empty(preview.Strings("affectedPublishedLotCodes"));

        // E4: without the answer nothing changes.
        var refused = await (await t.Manager.Client.PutSeedingTraceAsync(t.SessionId, t.Session.SeedingCorrection(date: earlier))).AgreementRefusalAsync();
        Assert.Equal(Agreement(t.LotCode), refused.Text("detail"));
        Assert.Equal([t.LotCode], refused.Strings("lotCodes"));
        Assert.Equal(Today.AddDays(-1), (await t.Owner.Client.GetSeedingAsync(t.SessionId)).Date("seedingDate"));
        Assert.Empty(await factory.AuditRowsAsync(TraceCorrectionTestKit.SeedingAction, t.SessionId));

        var saved = await (await t.Manager.Client.PutSeedingTraceAsync(t.SessionId, t.Session.SeedingCorrection(date: earlier, ownerApproved: true))).ReadAsync(HttpStatusCode.OK);

        Assert.Equal(("MSG-GEN-05", "Seeding session saved.", earlier), (saved.Text("messageCode"), saved.Text("message"), saved.GetProperty("record").Date("seedingDate")));
        var row = Assert.Single(await factory.AuditRowsAsync(TraceCorrectionTestKit.SeedingAction, t.SessionId));
        Assert.Equal((TraceCorrectionTestKit.Reason, t.Manager.Account.Id), (row.Reason, row.ActorAccountId));
        Assert.Contains($"\"SeedingDate\":\"{Today.AddDays(-1):yyyy-MM-dd}\"", row.Before);
        Assert.Contains($"\"SeedingDate\":\"{earlier:yyyy-MM-dd}\"", row.After);
        Assert.Contains("\"AffectedPublishedLotCodes\":[]", row.After);
        Assert.Contains("\"OwnerApprovalConfirmed\":true", row.After);

        // The cultivation screen stays locked for this session (BR-CYCLE-02): TRACE-03 relaxed nothing there.
        var locked = await (await t.Owner.Client.PutSeedingAsync(t.SessionId, saved.GetProperty("record").AsCorrection())).ReadAsync(HttpStatusCode.Conflict);
        Assert.Equal("MSG-GEN-09", locked.Text("code"));
    }

    [Fact]
    public async Task The_owner_corrects_with_a_confirmation_only_and_a_published_lot_shows_it_and_stays_published()
    {
        var t = await factory.TracedRaftAsync();
        await factory.PublishLotAsync(t.LotId);
        var publishedAt = (await factory.LotRowAsync(t.LotId)).PublishedAt;
        var other = await t.Owner.Client.SeedLotAsync();

        var preview = await (await t.Owner.Client.PreviewSeedingAsync(t.SessionId, t.Session.SeedingCorrection(seedLotId: other.LineId))).ReadAsync(HttpStatusCode.OK);
        Assert.Equal((false, "MSG-GEN-04", "Are you sure you want to correct traceability data? This action is recorded in the audit log."),
            (preview.Flag("needsOwnerApproval"), preview.Text("confirmMessageCode"), preview.Text("confirmMessage")));
        Assert.Equal(["seedLot"], preview.Strings("changedFields"));
        Assert.Equal([t.LotCode], preview.Strings("affectedPublishedLotCodes"));

        var saved = await (await t.Owner.Client.PutSeedingTraceAsync(t.SessionId, t.Session.SeedingCorrection(seedLotId: other.LineId))).ReadAsync(HttpStatusCode.OK);

        Assert.Equal(other.Code, saved.GetProperty("record").GetProperty("seedLot").Text("code"));
        Assert.Equal([t.LotCode], saved.Strings("affectedPublishedLotCodes"));
        var row = Assert.Single(await factory.AuditRowsAsync(TraceCorrectionTestKit.SeedingAction, t.SessionId));
        Assert.Contains($"\"AffectedPublishedLotCodes\":[\"{t.LotCode}\"]", row.After);
        Assert.Contains("\"OwnerApprovalConfirmed\":false", row.After);
        // No publication versions: the lot keeps its publish time and its state (BR-TRACE-05).
        var lot = await factory.LotRowAsync(t.LotId);
        Assert.Equal((publishedAt, (DateTimeOffset?)null, HarvestLotOriginState.Published), (lot.PublishedAt, lot.WithdrawnAt, lot.OriginState));
    }

    [Fact]
    public async Task A_withdrawn_lot_stays_withdrawn_and_is_not_named_as_a_published_lot()
    {
        var t = await factory.TracedRaftAsync();
        await factory.PublishLotAsync(t.LotId, withdrawn: true);
        var before = await factory.LotRowAsync(t.LotId);

        var preview = await (await t.Owner.Client.PreviewSeedingAsync(t.SessionId, t.Session.SeedingCorrection(date: Today.AddDays(-3)))).ReadAsync(HttpStatusCode.OK);
        Assert.Equal("Withdrawn", Assert.Single(preview.GetProperty("affectedLots").EnumerateArray()).Text("originState"));
        var saved = await (await t.Owner.Client.PutSeedingTraceAsync(t.SessionId, t.Session.SeedingCorrection(date: Today.AddDays(-3)))).ReadAsync(HttpStatusCode.OK);

        Assert.Empty(saved.Strings("affectedPublishedLotCodes"));
        var after = await factory.LotRowAsync(t.LotId);
        Assert.Equal((before.PublishedAt, before.WithdrawnAt, HarvestLotOriginState.Withdrawn), (after.PublishedAt, after.WithdrawnAt, after.OriginState));
        Assert.Equal("Withdrawn", (await t.Owner.Client.GetHarvestLotAsync(t.LotId)).Text("originState"));
    }

    [Fact]
    public async Task Taking_the_last_seed_lot_off_a_lane_of_a_lot_answers_msg_trace_03_and_a_lane_seeded_again_elsewhere_may_go()
    {
        var raft = await factory.SeededRaftAsync(1, 2);
        var owner = raft.Owner.Client;
        // Lane 1 is hung again from another session (re-hanging), lane 3 for the first time; then lane 1 and 2 are harvested.
        await owner.SeedingRecordAsync(raft.CycleId, raft.Lot.Session([1, 3], Today));
        var lot = await owner.HarvestLotAsync(raft.RaftId, HarvestFields.OfLanes(1, 2));

        var refused = await (await owner.PutSeedingTraceAsync(raft.Session.Id(), raft.Session.SeedingCorrection(lanes: [1]))).ReadAsync(HttpStatusCode.BadRequest);
        Assert.Equal("MSG-TRACE-03", refused.Text("code"));
        Assert.Equal(
            $"The correction is not saved: lane 2 of harvest lot {lot.Text("lotCode")} would have no seed lot. Please keep at least one seed lot for this lane.", refused.Text("detail"));
        var lane = Assert.Single(refused.GetProperty("lanes").EnumerateArray());
        Assert.Equal((lot.Text("lotCode"), 2), (lane.Text("lotCode"), lane.GetProperty("laneNumber").GetInt32()));
        Assert.Equal([1, 2], (await owner.GetSeedingAsync(raft.Session.Id())).Lanes());

        // Lane 1 keeps the seed lot of the other session, so it may leave this one.
        var saved = await (await owner.PutSeedingTraceAsync(raft.Session.Id(), raft.Session.SeedingCorrection(lanes: [2]))).ReadAsync(HttpStatusCode.OK);
        Assert.Equal([2], saved.GetProperty("record").Lanes());
        Assert.Equal([lot.Text("lotCode")], (await (await owner.PreviewSeedingAsync(raft.Session.Id(), saved.GetProperty("record").SeedingCorrection(lanes: [1, 2])))
            .ReadAsync(HttpStatusCode.OK)).GetProperty("affectedLots").EnumerateArray().Select(l => l.Text("lotCode")));
    }

    [Fact]
    public async Task The_seeding_rules_and_the_reason_are_checked_before_anything_is_saved()
    {
        var t = await factory.TracedRaftAsync();
        var owner = t.Owner.Client;

        Assert.Equal(["Seeding date", "Quantity", "Reason"],
            await (await owner.PutSeedingTraceAsync(t.SessionId, t.Session.SeedingCorrection(date: Today.AddDays(1), quantity: 0m, reason: " "))).InvalidFieldsAsync());
        Assert.Equal(["Seeding date"], await (await owner.PutSeedingTraceAsync(t.SessionId, t.Session.SeedingCorrection(date: Today.AddDays(-11)))).InvalidFieldsAsync());
        Assert.Equal(["Lanes"], await (await owner.PutSeedingTraceAsync(t.SessionId, t.Session.SeedingCorrection(lanes: [1, 1]))).InvalidFieldsAsync());

        // Moved to today, the day lane 1 is harvested: seed is not hung on a lane Being harvested (BR-CYCLE-02); the preview answers the same.
        var lanes = await (await owner.PutSeedingTraceAsync(t.SessionId, t.Session.SeedingCorrection(date: Today))).ReadAsync(HttpStatusCode.BadRequest);
        Assert.Equal(("MSG-GEN-01", "Lanes", $"Lanes 1 cannot be seeded on {Today:dd/MM/yyyy}."), (lanes.Text("code"), lanes.Text("field"), lanes.Text("hint")));
        Assert.Equal(HttpStatusCode.BadRequest, (await owner.PreviewSeedingAsync(t.SessionId, t.Session.SeedingCorrection(date: Today))).StatusCode);

        Assert.Equal(HttpStatusCode.Conflict, (await owner.PutSeedingTraceAsync(t.SessionId, t.Session.SeedingCorrection(quantity: 12m, rowVersion: AcsTestKit.StaleRowVersion))).StatusCode);
        Assert.Empty(await factory.AuditRowsAsync(TraceCorrectionTestKit.SeedingAction, t.SessionId));
    }

    [Fact]
    public async Task A_correction_of_the_quantity_alone_changes_no_lot_and_one_that_changes_nothing_writes_no_audit_row()
    {
        var t = await factory.TracedRaftAsync();

        var same = await (await t.Owner.Client.PreviewSeedingAsync(t.SessionId, t.Session.SeedingCorrection())).ReadAsync(HttpStatusCode.OK);
        Assert.Empty(same.Strings("changedFields"));
        (await t.Owner.Client.PutSeedingTraceAsync(t.SessionId, t.Session.SeedingCorrection())).EnsureSuccessStatusCode();
        Assert.Empty(await factory.AuditRowsAsync(TraceCorrectionTestKit.SeedingAction, t.SessionId));

        // The provenance page shows no quantity (BR-ORIGIN-04): MSG-TRACE-04 then names no lot.
        var quantity = await (await t.Manager.Client.PutSeedingTraceAsync(t.SessionId, t.Session.SeedingCorrection(quantity: 12.5m))).AgreementRefusalAsync();
        Assert.Equal("Has the Cooperative Owner agreed in person that the data is wrong?", quantity.Text("detail"));
        var saved = await (await t.Manager.Client.PutSeedingTraceAsync(t.SessionId, t.Session.SeedingCorrection(quantity: 12.5m, ownerApproved: true))).ReadAsync(HttpStatusCode.OK);
        Assert.Equal(12.5m, saved.GetProperty("record").GetProperty("quantity").GetDecimal());
        Assert.Single(await factory.AuditRowsAsync(TraceCorrectionTestKit.SeedingAction, t.SessionId));
    }

    [Fact]
    public async Task A_session_of_an_open_cycle_with_no_lane_in_a_lot_is_corrected_in_cycle_02_not_here()
    {
        var raft = await factory.SeededRaftAsync();

        var refused = await (await raft.Owner.Client.PutSeedingTraceAsync(raft.Session.Id(), raft.Session.SeedingCorrection(quantity: 12m))).ReadAsync(HttpStatusCode.Conflict);

        Assert.Equal("MSG-GEN-09", refused.Text("code"));
        Assert.EndsWith("is not in a harvest lot and cannot be used for this action.", refused.Text("detail"));
        Assert.Equal(HttpStatusCode.Conflict, (await raft.Owner.Client.PreviewSeedingAsync(raft.Session.Id(), raft.Session.SeedingCorrection(quantity: 12m))).StatusCode);
    }
}
