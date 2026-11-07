using System.Net;
using Microsoft.EntityFrameworkCore;
using OMTS.Api.IntegrationTests.Cultivation;
using OMTS.Api.IntegrationTests.Farming;
using OMTS.Api.IntegrationTests.Harvest;
using OMTS.Api.IntegrationTests.Infrastructure;
using PurchasingTestKit = OMTS.Api.IntegrationTests.Purchasing.PurchasingTestKit;
using ReturnBody = OMTS.Api.IntegrationTests.Purchasing.ReturnBody;
using ReturnLineBody = OMTS.Api.IntegrationTests.Purchasing.ReturnLineBody;
using OMTS.Domain.Constants;
using OMTS.Domain.Entities.Farming;
using OMTS.Domain.Entities.Harvest;
using OMTS.Domain.Enums;
using static OMTS.Api.IntegrationTests.Farming.FarmingTestKit;
using static OMTS.Api.IntegrationTests.Traceability.TraceCorrectionTestKit;

namespace OMTS.Api.IntegrationTests.Traceability;

/// <summary>
/// QA of TRACE-03 (independent of the developer's tests): the correctable set and the rules of the use case that first recorded each record (BR-TRACE-04), the seed-lot rules on a
/// corrected session (BR-CYCLE-02, GBR-04), MSG-TRACE-03/04, the publication state (BR-TRACE-05) and read-only rafts and zones (GBR-01).
/// </summary>
public class TraceCorrectionsQaTests(AuthApiFactory factory) : IClassFixture<AuthApiFactory>
{
    /// <summary>The traced raft (lanes 1 to 5 seeded yesterday, lane 1 harvested today) with a log of today, closed early today by the Owner.</summary>
    private async Task<(TracedRaft Raft, Guid LogId)> ClosedAsync()
    {
        var t = await factory.TracedRaftAsync();
        var log = await t.Owner.Client.RecordLogAsync(t.CycleId, new LogFields { Notes = "Buoys fine" });
        await t.Owner.Client.CloseCycleAsync(t.CycleId);
        return (t, log.Id());
    }

    private Task<Guid> LaneIdAsync(Guid raftId, int laneNumber) =>
        factory.QueryAsync(db => db.Lanes.Where(l => l.RaftId == raftId && l.LaneNumber == laneNumber).Select(l => l.Id).SingleAsync());

    /// <summary>
    /// DEFECT (Medium) – BR-FARM-04: lanes are removed only when the raft has no open cycle and a removed lane keeps its records; BR-TRACE-04: the harvest data of a Closed cycle
    /// is corrected only in TRACE-03. Today the lot correction re-checks its own lanes as "In Use" (HarvestLotFieldReader), so once lane 1 is removed after the close the cages of
    /// the lot can never be corrected again (developer deviation 8). The seeding correction keeps such a lane, so the two disagree.
    /// </summary>
    [Fact]
    public async Task A_lot_whose_lane_was_removed_after_the_close_can_still_have_its_cages_corrected()
    {
        var (t, _) = await ClosedAsync();
        await factory.UpdateAsync<Lane>(await LaneIdAsync(t.RaftId, 1), l => l.StatusId = StatusIds.Lane.Removed);

        // The session keeps the removed lane 1: allowed.
        await (await t.Owner.Client.PutSeedingTraceAsync(t.SessionId, t.Session.SeedingCorrection(quantity: 12m))).ReadAsync(HttpStatusCode.OK);

        var saved = await (await factory.SendLotTraceAsync(t.Owner.Client, t.LotId, HarvestFields.OfLanes(1) with { Cages = "9" })).ReadAsync(HttpStatusCode.OK);
        Assert.Equal(9, saved.GetProperty("lot").GetProperty("cageCount").GetInt32());
    }

    /// <summary>
    /// DEFECT (Low) – BR-ORIGIN-03/04: the origin information of a lot holds no cage count and no kg, so a cages-only correction changes no origin information; BR-TRACE-05: the audit
    /// log records the Published lots a correction changed. The seeding side already applies this (quantity alone names no lot); the lot side names the lot for any change, so
    /// MSG-TRACE-04 tells the Zone Manager the origin information of the lot changes and the audit row lists it as a changed Published lot.
    /// </summary>
    [Fact]
    public async Task A_cages_only_correction_of_a_published_lot_names_no_lot_whose_origin_changes()
    {
        var (t, _) = await ClosedAsync();
        await factory.PublishLotAsync(t.LotId);

        var preview = await (await t.Manager.Client.PreviewLotAsync(t.LotId,
            new { laneNumbers = new[] { 1 }, finishedLaneNumbers = Array.Empty<int>(), cageCount = 9, reason = Reason })).ReadAsync(HttpStatusCode.OK);

        Assert.Equal(["cageCount", "estimatedKg"], preview.Strings("changedFields"));
        Assert.Empty(preview.Strings("affectedPublishedLotCodes"));
        Assert.Equal("Has the Cooperative Owner agreed in person that the data is wrong?", preview.Text("confirmMessage"));
    }

    [Fact]
    public async Task A_lot_correction_leaves_a_withdrawn_lot_withdrawn_and_a_republished_lot_published()
    {
        var (t, _) = await ClosedAsync();
        await factory.PublishLotAsync(t.LotId, withdrawn: true);
        var withdrawn = await factory.LotRowAsync(t.LotId);

        // BR-TRACE-05: a correction never changes the origin state; a Withdrawn lot is not one of the Published lots it changed.
        var first = await (await factory.SendLotTraceAsync(t.Owner.Client, t.LotId, HarvestFields.OfLanes(1, 2))).ReadAsync(HttpStatusCode.OK);
        Assert.Empty(first.Strings("affectedPublishedLotCodes"));
        var afterFirst = await factory.LotRowAsync(t.LotId);
        Assert.Equal((withdrawn.PublishedAt, withdrawn.WithdrawnAt, HarvestLotOriginState.Withdrawn), (afterFirst.PublishedAt, afterFirst.WithdrawnAt, afterFirst.OriginState));
        Assert.Contains("\"AffectedPublishedLotCodes\":[]", Assert.Single(await factory.AuditRowsAsync(LotAction, t.LotId)).After);

        // BR-TRACE-02: the Owner publishes it again (a publish time later than the withdrawal); the next correction names it and keeps it Published.
        var republishedAt = DateTimeOffset.UtcNow;
        await factory.UpdateAsync<HarvestLot>(t.LotId, l => l.PublishedAt = republishedAt);
        var second = await (await factory.SendLotTraceAsync(t.Owner.Client, t.LotId, HarvestFields.OfLanes(1, 2, 3))).ReadAsync(HttpStatusCode.OK);
        Assert.Equal([t.LotCode], second.Strings("affectedPublishedLotCodes"));
        var afterSecond = await factory.LotRowAsync(t.LotId);
        Assert.Equal((HarvestLotOriginState.Published, withdrawn.WithdrawnAt), (afterSecond.OriginState, afterSecond.WithdrawnAt));
        Assert.Equal(republishedAt.ToUnixTimeMilliseconds(), afterSecond.PublishedAt!.Value.ToUnixTimeMilliseconds());
    }

    [Fact]
    public async Task A_corrected_session_follows_the_seed_lot_rules_and_counts_as_seeded_for_purchase_returns()
    {
        var t = await factory.TracedRaftAsync();
        var owner = t.Owner.Client;

        // BR-CYCLE-02: not before the date the seed lot was received; GBR-04: only lines of Completed receipts, and not a lot returned in full.
        var receivedToday = await owner.SeedLotAsync(received: Today);
        Assert.Equal(["Seeding date"], await (await owner.PutSeedingTraceAsync(t.SessionId, t.Session.SeedingCorrection(seedLotId: receivedToday.LineId))).InvalidFieldsAsync());
        var draft = await owner.SeedLotAsync(complete: false);
        Assert.Equal(["Seed lot"], await (await owner.PutSeedingTraceAsync(t.SessionId, t.Session.SeedingCorrection(seedLotId: draft.LineId))).InvalidFieldsAsync());
        var returnedInFull = await owner.SeedLotAsync();
        await owner.ReturnLotAsync(returnedInFull, 100m);
        Assert.Equal(["Seed lot"], await (await owner.PutSeedingTraceAsync(t.SessionId, t.Session.SeedingCorrection(seedLotId: returnedInFull.LineId))).InvalidFieldsAsync());

        // GBR-04: seeding never compares the quantity with what is left, so a lot returned in part is still offered.
        var mostlyReturned = await owner.SeedLotAsync();
        await owner.ReturnLotAsync(mostlyReturned, 95m);
        var moved = await (await owner.PutSeedingTraceAsync(t.SessionId, t.Session.SeedingCorrection(seedLotId: mostlyReturned.LineId))).ReadAsync(HttpStatusCode.OK);
        Assert.Equal(mostlyReturned.Code, moved.GetProperty("record").GetProperty("seedLot").Text("code"));

        // The corrected session counts in the quantity seeded of its new lot: returns are limited to received minus seeded (GBR-04, PURCHASE-02).
        var fresh = await owner.SeedLotAsync();
        await (await owner.PutSeedingTraceAsync(t.SessionId, moved.GetProperty("record").SeedingCorrection(seedLotId: fresh.LineId))).ReadAsync(HttpStatusCode.OK);
        Assert.Equal(HttpStatusCode.BadRequest,
            (await PurchasingTestKit.PostReturnAsync(owner, fresh.ReceiptId, new ReturnBody { Lines = [new ReturnLineBody(fresh.LineId, 95m)] })).StatusCode);
        await owner.ReturnLotAsync(fresh, 90m);
        // The lot it left is free again: its last 5 can be returned.
        await owner.ReturnLotAsync(mostlyReturned, 5m);
    }

    [Fact]
    public async Task Msg_trace_03_names_every_lane_and_lot_and_msg_trace_04_every_lot_whose_origin_changes()
    {
        var raft = await factory.SeededRaftAsync(1, 2, 3);
        var owner = raft.Owner.Client;
        var first = (await owner.HarvestLotAsync(raft.RaftId, HarvestFields.OfLanes(1, 2))).Text("lotCode");
        var second = (await owner.HarvestLotAsync(raft.RaftId, HarvestFields.OfLanes(3))).Text("lotCode");

        // TRACE-03.0.E3: "names the lanes and harvest lots that would have no seed lot".
        var refused = await (await owner.PutSeedingTraceAsync(raft.Session.Id(), raft.Session.SeedingCorrection(lanes: [1]))).ReadAsync(HttpStatusCode.BadRequest);
        Assert.Equal("MSG-TRACE-03", refused.Text("code"));
        Assert.Equal($"The correction is not saved: lane 2 of harvest lot {first} would have no seed lot. Please keep at least one seed lot for this lane.", refused.Text("detail"));
        Assert.Equal([(first, 2), (second, 3)], refused.GetProperty("lanes").EnumerateArray().Select(l => (l.Text("lotCode"), l.GetProperty("laneNumber").GetInt32())));

        // MSG-TRACE-04 {lot_codes}: a new date changes the origin of every lane of the session, so both lots, the earliest first.
        var manager = await factory.ManagerAsync(raft.ZoneId);
        var agreement = await (await manager.Client.PutSeedingTraceAsync(raft.Session.Id(), raft.Session.SeedingCorrection(date: Today.AddDays(-2)))).AgreementRefusalAsync();
        Assert.Equal($"This correction changes the origin information of harvest lots {first}, {second}. Has the Cooperative Owner agreed in person that the data is wrong?",
            agreement.Text("detail"));
        Assert.Equal([first, second], agreement.Strings("lotCodes"));
        Assert.Empty(await factory.AuditRowsAsync(SeedingAction, raft.Session.Id()));
    }

    [Fact]
    public async Task The_harvest_time_the_lot_code_and_the_kg_per_cage_cannot_be_sent_and_the_estimate_uses_the_lots_own_kg_per_cage()
    {
        using var api = new AuthApiFactory();
        var t = await api.TracedRaftAsync();
        await t.Owner.Client.CloseCycleAsync(t.CycleId);
        var before = await api.LotRowAsync(t.LotId);
        // BR-HARVEST-02: a later change of the harvest settings does not change the kg per cage of the lot.
        await t.Owner.Client.SetHarvestSettingsAsync(before.KgPerCage + 27m, 3);

        var form = new HarvestCorrection(HarvestFields.OfLanes(1) with { Cages = "10" }, Reason, await api.LotVersionAsync(t.LotId)).ToForm();
        form.Add(new StringContent("2020-01-01T08:00:00+07:00"), "harvestedAt");
        form.Add(new StringContent("2020-01-01"), "harvestDate");
        form.Add(new StringContent("FAKE-20200101"), "lotCode");
        form.Add(new StringContent("1"), "kgPerCage");
        await (await t.Owner.Client.PutAsync($"/api/harvest-lots/{t.LotId}/trace-correction", form)).ReadAsync(HttpStatusCode.OK);

        var after = await api.LotRowAsync(t.LotId);
        Assert.Equal((before.LotCode, before.HarvestedAt, before.HarvestDate, before.KgPerCage), (after.LotCode, after.HarvestedAt, after.HarvestDate, after.KgPerCage));
        Assert.Equal((10, 10 * before.KgPerCage), (after.CageCount, after.EstimatedKg));
    }

    [Fact]
    public async Task Every_reason_is_required_and_at_most_1000_characters()
    {
        var (t, logId) = await ClosedAsync();
        var owner = t.Owner.Client;
        var keys = (await owner.GetLogAsync(logId)).PhotoKeys();
        var tooLong = new string('x', 1001);

        Assert.Equal(["Reason"], await (await factory.SendLotTraceAsync(owner, t.LotId, HarvestFields.OfLanes(1) with { Cages = "9" }, reason: "   ")).InvalidFieldsAsync());
        Assert.Equal(["Reason"], await (await factory.SendLotTraceAsync(owner, t.LotId, HarvestFields.OfLanes(1) with { Cages = "9" }, reason: tooLong)).InvalidFieldsAsync());
        Assert.Equal(["Reason"], await (await owner.PutLogTraceAsync(logId, new LogFields { KeepPhotoKeys = keys, Photos = [], Reason = "  " })).InvalidFieldsAsync());
        Assert.Equal(["Reason"], await (await owner.PutLogTraceAsync(logId, new LogFields { KeepPhotoKeys = keys, Photos = [], Reason = tooLong })).InvalidFieldsAsync());
        Assert.Equal(["Reason"], await (await owner.PutSeedingTraceAsync(t.SessionId, t.Session.SeedingCorrection(quantity: 12m, reason: tooLong))).InvalidFieldsAsync());
        Assert.Equal(["Reason"], await (await owner.PutSeedingTraceAsync(t.SessionId, t.Session.SeedingCorrection(quantity: 12m, reason: null))).InvalidFieldsAsync());

        Assert.Equal(8, (await factory.LotRowAsync(t.LotId)).CageCount);
        await (await factory.SendLotTraceAsync(owner, t.LotId, HarvestFields.OfLanes(1) with { Cages = "9" }, reason: new string('x', 1000))).ReadAsync(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Records_are_corrected_never_removed_and_after_a_correction_the_cultivation_screens_stay_locked()
    {
        var (t, logId) = await ClosedAsync();
        var owner = t.Owner.Client;
        var keys = (await owner.GetLogAsync(logId)).PhotoKeys();
        await (await owner.PutLogTraceAsync(logId, new LogFields { Notes = "Corrected", KeepPhotoKeys = keys, Photos = [], Reason = Reason })).ReadAsync(HttpStatusCode.OK);
        await (await factory.SendLotTraceAsync(owner, t.LotId, HarvestFields.OfLanes(1) with { Cages = "9" })).ReadAsync(HttpStatusCode.OK);
        await (await owner.PutSeedingTraceAsync(t.SessionId, t.Session.SeedingCorrection(quantity: 12m))).ReadAsync(HttpStatusCode.OK);

        // BR-TRACE-04: no removal under TRACE-03.
        foreach (var route in new[] { $"/api/seeding-records/{t.SessionId}/trace-correction", $"/api/cultivation-logs/{logId}/trace-correction", $"/api/harvest-lots/{t.LotId}/trace-correction" })
        {
            Assert.Contains((await owner.DeleteAsync(route)).StatusCode, new[] { HttpStatusCode.NotFound, HttpStatusCode.MethodNotAllowed });
            Assert.Contains((await owner.PostAsync($"{route}/remove", null)).StatusCode, new[] { HttpStatusCode.NotFound, HttpStatusCode.MethodNotAllowed });
        }

        // BR-CYCLE-04 / BR-HARVEST-05: the cultivation and harvest screens stay locked after the close, also after a TRACE-03 correction.
        var session = await owner.GetSeedingAsync(t.SessionId);
        var logVersion = await factory.LogVersionAsync(logId);
        foreach (var answer in new[]
        {
            await owner.PutSeedingAsync(t.SessionId, session.AsCorrection() with { Quantity = 7m }),
            await owner.RemoveSeedingAsync(t.SessionId, rowVersion: session.Text("rowVersion")),
            await owner.PutLogAsync(logId, new LogFields { Notes = "late", Reason = "late", RowVersion = logVersion, KeepPhotoKeys = keys, Photos = [] }),
            await owner.RemoveLogAsync(logId, "late", logVersion),
            await factory.SendCorrectionAsync(owner, t.LotId, HarvestFields.OfLanes(1) with { Cages = "3" }),
            await factory.SendRemovalAsync(owner, t.LotId),
        })
        {
            Assert.Equal((HttpStatusCode.Conflict, "MSG-GEN-09"), (answer.StatusCode, await answer.ErrorCodeAsync()));
        }

        Assert.Equal((12m, 9, "Corrected"), (
            (await owner.GetSeedingAsync(t.SessionId)).GetProperty("quantity").GetDecimal(),
            (await factory.LotRowAsync(t.LotId)).CageCount,
            (await owner.GetLogAsync(logId)).Text("notes")));
    }

    [Fact]
    public async Task A_discontinued_zone_keeps_its_records_read_only_but_the_screen_still_lists_them()
    {
        var (t, logId) = await ClosedAsync();
        var owner = t.Owner.Client;
        var keys = (await owner.GetLogAsync(logId)).PhotoKeys();
        await factory.UpdateAsync<FarmingZone>(t.ZoneId, z => z.StatusId = StatusIds.Usage.Discontinued);

        foreach (var answer in new[]
        {
            await owner.PutSeedingTraceAsync(t.SessionId, t.Session.SeedingCorrection(quantity: 12m)),
            await owner.PutLogTraceAsync(logId, new LogFields { Notes = "x", KeepPhotoKeys = keys, Photos = [], Reason = Reason }),
            await factory.SendLotTraceAsync(owner, t.LotId, HarvestFields.OfLanes(1) with { Cages = "9" }),
        })
        {
            Assert.Equal((HttpStatusCode.Conflict, "MSG-GEN-09"), (answer.StatusCode, await answer.ErrorCodeAsync()));
        }

        // GBR-01: a discontinued record keeps its history and can still be viewed.
        Assert.Single((await t.Manager.Client.TraceCyclesAsync(t.RaftId)).GetProperty("cycles").EnumerateArray());
        Assert.Single((await t.Manager.Client.TraceRecordsAsync(t.CycleId)).GetProperty("harvestLots").EnumerateArray());
        Assert.Empty(await factory.AuditRowsAsync(LogAction, logId));
    }

    [Fact]
    public async Task An_anonymous_caller_is_refused()
    {
        var t = await factory.TracedRaftAsync();
        using var anonymous = factory.CreateClient();

        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync($"/api/rafts/{t.RaftId}/trace-corrections")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.PutSeedingTraceAsync(t.SessionId, t.Session.SeedingCorrection(quantity: 12m, ownerApproved: true))).StatusCode);
        Assert.Equal(10m, (await t.Owner.Client.GetSeedingAsync(t.SessionId)).GetProperty("quantity").GetDecimal());
    }
}
