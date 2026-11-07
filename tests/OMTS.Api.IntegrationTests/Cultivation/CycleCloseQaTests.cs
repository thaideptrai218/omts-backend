using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using OMTS.Api.IntegrationTests.Farming;
using OMTS.Api.IntegrationTests.Harvest;
using OMTS.Api.IntegrationTests.Infrastructure;
using static OMTS.Api.IntegrationTests.Farming.FarmingTestKit;

namespace OMTS.Api.IntegrationTests.Cultivation;

/// <summary>
/// QA of PR C5 (CYCLE-06, CYCLE-05 closed views) against the SRS: the early/normal rule of BR-CYCLE-07, the figures of BR-CYCLE-06, the input of CYCLE-06.0.E1, what the
/// close locks (BR-CYCLE-04) and what it leaves free (BR-CYCLE-03, BR-FARM-04, CYCLE-06 POST-4). The in-memory API; the races are in <see cref="CycleCloseQaTestsSqlServer"/>.
/// </summary>
public class CycleCloseQaTests(AuthApiFactory factory) : IClassFixture<AuthApiFactory>
{
    private const string Msg07Tail =
        "Oysters left on lanes that are Growing cannot be harvested in OMTS after the close. Closing it now needs the Cooperative Owner's approval. Has the Cooperative Owner approved closing it now?";

    // BR-CYCLE-07: normal when at least one session and every lane WITH SEED is Harvested; lanes never seeded do not make it early.
    [Fact]
    public async Task Every_seeded_lane_harvested_is_a_normal_close_even_with_lanes_never_seeded()
    {
        var raft = await factory.SeededRaftAsync(1, 2);
        await raft.Owner.Client.HarvestLotAsync(raft.RaftId, HarvestFields.OfLanes(1, 2).FinishedLanes(1, 2) with { EstimatedKg = "210.5" });
        var manager = await factory.ManagerAsync(raft.ZoneId);

        var preview = await manager.Client.ClosePreviewAsync(raft.CycleId);
        Assert.Equal((false, false), (preview.Flag("isEarly"), preview.Flag("needsOwnerApproval")));
        Assert.True(preview.IsNull("earlyCloseMessageCode"));

        var closed = await manager.Client.CloseCycleAsync(raft.CycleId);

        var summary = closed.GetProperty("summary");
        Assert.False(closed.GetProperty("cycle").Flag("closedEarly"));
        Assert.Empty(summary.Numbers("notHarvestedLanes"));
        Assert.True(summary.IsNull("earlyReason"));
        // BR-CYCLE-06: seed hung = total of the sessions; kg = estimated kg of the lots; days from the start (10 days ago) to the last lot (today), both counted (PM 03/11).
        Assert.Equal((210.5m, 10m, Today, 11), (summary.Number("kgHarvested"), summary.Number("seedHung"), summary.Date("lastHarvestDate"), summary.GetProperty("daysOfFarming").GetInt32()));
        Assert.Equal(11, closed.GetProperty("cycle").GetProperty("daysOfFarming").GetInt32());
        Assert.Contains("\"OwnerApprovalConfirmed\":false", Assert.Single(await factory.CloseAuditAftersAsync(raft.CycleId)));
    }

    // BR-CYCLE-07: a lane Being harvested is not Harvested, so the close is early; the kg already recorded in its lots counts in the summary. MSG-CYCLE-07 names every such lane.
    [Fact]
    public async Task A_lane_still_being_harvested_makes_the_close_early_and_its_kg_count()
    {
        var raft = await factory.SeededRaftAsync(1, 2, 3);
        await raft.Owner.Client.HarvestLotAsync(raft.RaftId, HarvestFields.OfLanes(1, 2).FinishedLanes(1) with { EstimatedKg = "120" });
        var manager = await factory.ManagerAsync(raft.ZoneId);

        var preview = await manager.Client.ClosePreviewAsync(raft.CycleId);
        Assert.True(preview.Flag("needsOwnerApproval"));
        Assert.Equal($"This cycle is not finished: lanes 2, 3 are not harvested. {Msg07Tail}", preview.Text("earlyCloseMessage"));

        // CYCLE-06.1.E1: without the approval nothing changes, and the 409 carries the same MSG-CYCLE-07 text.
        var refused = await (await manager.Client.SendCloseAsync(raft.CycleId)).ReadAsync(HttpStatusCode.Conflict);
        Assert.Equal(("MSG-CYCLE-07", preview.Text("earlyCloseMessage")), (refused.Text("code"), refused.Text("detail")));
        Assert.Equal([2, 3], refused.Numbers("laneNumbers"));
        Assert.Empty(await factory.CloseAuditAftersAsync(raft.CycleId));

        var closed = await manager.Client.CloseCycleAsync(raft.CycleId, ownerApproved: true);
        var summary = closed.GetProperty("summary");
        Assert.Equal((true, "LanesNotHarvested", 120m), (summary.Flag("closedEarly"), summary.Text("earlyReason"), summary.Number("kgHarvested")));
        Assert.Equal([2, 3], summary.Numbers("notHarvestedLanes"));
        Assert.Contains("\"OwnerApprovalConfirmed\":true", Assert.Single(await factory.CloseAuditAftersAsync(raft.CycleId)));
    }

    // BR-CYCLE-06: the summary is worked out from the records as they are, so it follows the HARVEST-01.1 corrections and removals (PR H1b) before the close.
    [Fact]
    public async Task The_summary_follows_the_corrected_and_removed_harvest_lots()
    {
        var raft = await factory.SeededRaftAsync(1, 2, 3);
        var owner = raft.Owner.Client;
        var first = await owner.HarvestLotAsync(raft.RaftId, HarvestFields.OfLanes(1) with { EstimatedKg = "100" });
        var second = await owner.HarvestLotAsync(raft.RaftId, HarvestFields.OfLanes(2) with { EstimatedKg = "50" });
        await factory.CorrectLotAsync(owner, first.Id(), HarvestFields.OfLanes(1) with { EstimatedKg = "80" });
        await factory.RemoveLotAsync(owner, second.Id());

        var open = await owner.CycleSummaryAsync(raft.CycleId);
        Assert.Equal((80m, 1), (open.Number("kgHarvested"), open.GetProperty("harvestLotCount").GetInt32()));
        // Lane 2 is back to Growing once its only lot is removed; lane 1 is still Being harvested.
        Assert.Equal([1, 2, 3], open.Numbers("notHarvestedLanes"));

        var closed = (await owner.CloseCycleAsync(raft.CycleId)).GetProperty("summary");
        Assert.Equal((80m, 1, Today), (closed.Number("kgHarvested"), closed.GetProperty("harvestLotCount").GetInt32(), closed.Date("lastHarvestDate")));
        Assert.Equal(closed.GetRawText(), (await owner.CycleSummaryAsync(raft.CycleId)).GetRawText());
    }

    // BR-CYCLE-06: an estimate that was not entered shows Not planned (null in the answer), before and after the close.
    [Fact]
    public async Task A_cycle_without_a_plan_shows_no_estimate_next_to_the_actual_figures()
    {
        var setup = await factory.CycleAsync();
        var owner = setup.Owner.Client;
        var lot = await owner.SeedLotAsync();
        await owner.RecordSeedingAsync(setup.CycleId, lot.Session([1]));

        foreach (var summary in new[] { (await owner.ClosePreviewAsync(setup.CycleId)).GetProperty("summary"), (await owner.CloseCycleAsync(setup.CycleId)).GetProperty("summary") })
        {
            Assert.True(summary.IsNull("expectedSeedQuantity") && summary.IsNull("expectedHarvestDate") && summary.IsNull("targetYieldKg"), summary.GetRawText());
            Assert.Equal(10m, summary.Number("seedHung"));
        }
    }

    // CYCLE-06.0.E1: the assessment is free text; the longest one accepted is kept whole.
    [Fact]
    public async Task An_assessment_of_2000_characters_is_kept_whole()
    {
        var raft = await factory.SeededRaftAsync();
        var text = new string('b', 2000);

        var closed = await raft.Owner.Client.CloseCycleAsync(raft.CycleId, text);

        Assert.Equal(text, closed.GetProperty("cycle").Text("assessment"));
        Assert.Equal(text, (await raft.Owner.Client.CycleSummaryAsync(raft.CycleId)).Text("assessment"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("not base64 !")]
    public async Task A_close_without_a_readable_version_answers_msg_gen_01_and_changes_nothing(string? version)
    {
        var raft = await factory.SeededRaftAsync();

        var refused = await (await raft.Owner.Client.PostCloseAsync(raft.CycleId, CycleCloseTestKit.Assessment, version)).ReadAsync(HttpStatusCode.BadRequest);

        Assert.Equal(("MSG-GEN-01", "RowVersion"), (refused.Text("code"), refused.Text("field")));
        Assert.Empty(await factory.CloseAuditAftersAsync(raft.CycleId));
    }

    // A cancelled cycle is removed (BR-CYCLE-01): its id answers 404 on every CYCLE-06 route, as an unknown id does.
    [Fact]
    public async Task A_cancelled_or_unknown_cycle_answers_not_found_on_every_close_route()
    {
        var setup = await factory.CycleAsync();
        var owner = setup.Owner.Client;
        var cycle = await owner.GetFromJsonAsync<JsonElement>($"/api/cultivation-cycles/{setup.CycleId}");
        Assert.Equal(HttpStatusCode.NoContent, (await owner.CancelCycleAsync(setup.CycleId, cycle.Text("rowVersion"))).StatusCode);

        foreach (var id in new[] { setup.CycleId, Guid.NewGuid() })
        {
            Assert.Equal(HttpStatusCode.NotFound, (await owner.GetClosePreviewAsync(id)).StatusCode);
            Assert.Equal(HttpStatusCode.NotFound, (await owner.PostCloseAsync(id, CycleCloseTestKit.Assessment, cycle.Text("rowVersion"), ownerApproved: true)).StatusCode);
            Assert.Equal(HttpStatusCode.NotFound, (await owner.GetAsync($"/api/cultivation-cycles/{id}/summary")).StatusCode);
        }
    }

    // BR-CYCLE-03/04: a usage may not be moved onto a day of a closed cycle; a day before the cycle started is outside it and stays free; the start day is inside.
    [Fact]
    public async Task After_the_close_usage_stays_free_only_outside_the_closed_cycle()
    {
        var raft = await factory.SeededRaftAsync();
        var owner = raft.Owner.Client;
        var otherRaftId = (await owner.CreateRaftAsync(raft.ZoneId)).Id();
        var item = await factory.ItemAsync();
        var elsewhere = await owner.RecordUsageAsync(otherRaftId, item, Today.AddDays(-2));
        await owner.RecordUsageAsync(raft.RaftId, item, Today.AddDays(-1));
        await owner.CloseCycleAsync(raft.CycleId);

        var moved = await owner.PutUsageAsync(elsewhere.Id(), elsewhere.AsEdit() with { RaftId = raft.RaftId });
        Assert.Equal((HttpStatusCode.Conflict, "MSG-GEN-09"), (moved.StatusCode, await moved.ErrorCodeAsync()));
        Assert.Equal(otherRaftId, await factory.QueryAsync(db => db.MaterialUsages.Where(u => u.Id == elsewhere.Id()).Select(u => u.RaftId).SingleAsync()));

        var startDay = await owner.PostUsageAsync(raft.RaftId, new UsageBody(Today.AddDays(-10), MaterialUsageTestKit.BrokenBuoy, [new UsageLine(item)]));
        Assert.Equal((HttpStatusCode.Conflict, "MSG-GEN-09"), (startDay.StatusCode, await startDay.ErrorCodeAsync()));
        var dayBefore = await owner.PostUsageAsync(raft.RaftId, new UsageBody(Today.AddDays(-11), MaterialUsageTestKit.BrokenBuoy, [new UsageLine(item)]));
        Assert.Equal(HttpStatusCode.Created, dayBefore.StatusCode);

        // The list shows the closed cycle's group locked and the group with no cycle free.
        var groups = (await owner.UsageListAsync(raft.RaftId)).GetProperty("groups").EnumerateArray().ToList();
        Assert.True(groups.Single(g => g.GetProperty("cycle").ValueKind != JsonValueKind.Null && g.GetProperty("cycle").Id() == raft.CycleId).Flag("locked"));
        Assert.False(groups.Single(g => g.GetProperty("cycle").ValueKind == JsonValueKind.Null).Flag("locked"));
    }

    // CYCLE-06 POST-4 and BR-FARM-04: with no Open cycle the lanes can be added or removed again; a removed lane keeps its records, so the closed summary does not change.
    [Fact]
    public async Task After_the_close_lanes_can_be_added_and_removed_and_the_closed_summary_keeps_its_lanes()
    {
        var raft = await factory.SeededRaftAsync();
        var owner = raft.Owner.Client;
        await owner.HarvestLotAsync(raft.RaftId, HarvestFields.OfLanes(1).FinishedLanes(1));
        var atClose = (await owner.CloseCycleAsync(raft.CycleId)).GetProperty("summary");
        Assert.Equal([2, 3, 4, 5], atClose.Numbers("notHarvestedLanes"));

        var view = await owner.GetFromJsonAsync<JsonElement>($"/api/rafts/{raft.RaftId}");
        var grown = await owner.EditRaftAsync(raft.RaftId, NewRaftBody(view.Text("code"), lanes: 6) with { RowVersion = view.Text("rowVersion") });
        await owner.EditRaftAsync(raft.RaftId, NewRaftBody(view.Text("code"), lanes: 4) with { RowVersion = grown.Text("rowVersion") });

        Assert.Equal(atClose.GetRawText(), (await owner.CycleSummaryAsync(raft.CycleId)).GetRawText());
    }

    // SCR-CYCLE-01 for the Zone Manager: Close cycle is offered only while the cycle is Open; after the close the views stay readable and the close routes answer MSG-GEN-09.
    [Fact]
    public async Task The_zone_manager_is_offered_close_only_while_open_and_still_reads_the_closed_cycle()
    {
        var raft = await factory.SeededRaftAsync();
        var manager = (await factory.ManagerAsync(raft.ZoneId)).Client;
        Assert.True((await manager.ZoneRowAsync(raft.ZoneId, raft.RaftId)).Flag("canClose"));

        await manager.CloseCycleAsync(raft.CycleId, ownerApproved: true);

        var row = await manager.ZoneRowAsync(raft.ZoneId, raft.RaftId);
        Assert.Equal((false, false, false), (row.Flag("canClose"), row.Flag("canEdit"), row.Flag("canStart")));
        Assert.Equal(CycleCloseTestKit.Assessment, (await manager.CycleSummaryAsync(raft.CycleId)).Text("assessment"));
        Assert.False((await manager.ProgressAsync(raft.RaftId, $"?cycleId={raft.CycleId}")).IsNull("summary"));
        Assert.Equal("MSG-GEN-09", await (await manager.GetClosePreviewAsync(raft.CycleId)).ErrorCodeAsync());
    }
}
