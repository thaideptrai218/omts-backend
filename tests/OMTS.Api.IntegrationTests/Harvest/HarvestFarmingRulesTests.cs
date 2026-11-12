using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using OMTS.Api.IntegrationTests.Cultivation;
using OMTS.Api.IntegrationTests.Farming;
using OMTS.Api.IntegrationTests.Infrastructure;
using OMTS.Domain.Entities.Cultivation;
using static OMTS.Api.IntegrationTests.Farming.FarmingTestKit;

namespace OMTS.Api.IntegrationTests.Harvest;

/// <summary>
/// PM decision 11/11/2026 on the farming flow: fractional cages, kg worked out from them, the target yield as a plan only (no cap), "finish harvest" on a lane, the lane
/// facts of the screen, finishing an empty seeding, the checks on map pins and the plan figures of the yield report.
/// </summary>
public class HarvestFarmingRulesTests(AuthApiFactory factory) : IClassFixture<AuthApiFactory>
{
    [Fact]
    public async Task Half_a_cage_is_saved_and_the_kg_is_worked_out_and_returned_with_the_cages()
    {
        var raft = await factory.SeededRaftAsync();

        var lot = await raft.Owner.Client.HarvestLotAsync(raft.RaftId, new HarvestFields { Cages = "0.5" });

        Assert.Equal((0.5m, 25m, 50m), (lot.Kg("cageCount"), lot.Kg("estimatedKg"), lot.Kg("kgPerCage")));
        var again = await raft.Owner.Client.GetHarvestLotAsync(lot.Id());
        Assert.Equal(0.5m, again.Kg("cageCount"));
    }

    [Theory]
    [InlineData("8.25", "412.5")]
    [InlineData("100000", "5000000")]
    public async Task Cages_with_two_decimals_up_to_100000_are_accepted(string cages, string kg)
    {
        var raft = await factory.SeededRaftAsync();

        var lot = await raft.Owner.Client.HarvestLotAsync(raft.RaftId, new HarvestFields { Cages = cages });

        Assert.Equal(decimal.Parse(kg, System.Globalization.CultureInfo.InvariantCulture), lot.Kg("estimatedKg"));
    }

    [Theory]
    [InlineData("100000.01")]
    [InlineData("1.005")]
    [InlineData("2,5")]
    public async Task Cages_above_the_maximum_or_with_a_third_decimal_are_refused(string cages)
    {
        var raft = await factory.SeededRaftAsync();

        var answer = await (await raft.Owner.Client.PostHarvestAsync(raft.RaftId, new HarvestFields { Cages = cages })).ReadAsync(HttpStatusCode.BadRequest);

        Assert.Equal(("MSG-GEN-01", "Number of cages"), (answer.Text("code"), answer.Text("field")));
        Assert.Equal(0, await factory.LotCountAsync(raft.RaftId));
    }

    // The estimated kg is only an estimate (SRS glossary): far from cages x kg per cage it warns, and only above 10x is it refused.
    [Theory]
    [InlineData("19.99", true)]
    [InlineData("20", false)]
    [InlineData("200", false)]
    [InlineData("200.01", true)]
    [InlineData("1000", true)]
    public async Task A_kg_sent_by_hand_far_from_cages_times_kg_per_cage_only_warns(string kg, bool warns)
    {
        var raft = await factory.SeededRaftAsync();

        var saved = await raft.Owner.Client.RecordHarvestAsync(raft.RaftId, new HarvestFields { Cages = "2", EstimatedKg = kg });

        var warnings = saved.GetProperty("warnings").EnumerateArray().ToList();
        Assert.Equal(warns, warnings.Count == 1);
        if (warns)
        {
            Assert.Equal("ESTIMATED_KG_FAR_FROM_CAGES", warnings[0].Text("code"));
        }
    }

    [Fact]
    public async Task A_kg_above_ten_times_cages_times_kg_per_cage_is_refused()
    {
        var raft = await factory.SeededRaftAsync();

        var error = await (await raft.Owner.Client.PostHarvestAsync(raft.RaftId, new HarvestFields { Cages = "2", EstimatedKg = "1000.01" })).ReadAsync(HttpStatusCode.BadRequest);

        Assert.Equal(("MSG-GEN-01", "Estimated kg"), (error.Text("code"), error.Text("field")));
        Assert.Contains("at most 1000", error.Text("hint"));
    }

    // Owner decision 12/11/2026: the target yield is a plan. 500 kg planned and 700 kg harvested is saved with no question, no reason and no refusal; only the screen says it is over.
    [Fact]
    public async Task A_harvest_far_above_the_target_saves_without_a_question_and_the_screen_says_it_is_over()
    {
        var raft = await factory.SeededRaftAsync();
        var owner = raft.Owner.Client;
        await factory.UpdateAsync<CultivationCycle>(raft.CycleId, c => c.TargetYieldKg = 500m);
        await owner.HarvestLotAsync(raft.RaftId, new HarvestFields { Cages = "10" }); // 500 kg of 500

        var lot = await owner.HarvestLotAsync(raft.RaftId, new HarvestFields { Cages = "4" }); // 700 kg of 500: +40 %
        var huge = await owner.HarvestLotAsync(raft.RaftId, new HarvestFields { Cages = "31" }); // 2250 kg: far above 150 %

        Assert.Equal(200m, lot.Kg("estimatedKg"));
        Assert.Equal(1550m, huge.Kg("estimatedKg"));
        Assert.Equal(JsonValueKind.Null, lot.GetProperty("overPlanReason").ValueKind);
        Assert.Equal(3, await factory.LotCountAsync(raft.RaftId));
        var screen = await owner.HarvestScreenAsync(raft.RaftId);
        Assert.Equal((2250m, 500m, -1750m, 450m, true), (screen.Kg("harvestedKg"), screen.Kg("targetYieldKg"), screen.Kg("remainingKg"), screen.Kg("targetPercent"), screen.GetProperty("overTarget").GetBoolean()));
    }

    [Fact]
    public async Task An_over_plan_confirmation_sent_by_an_older_client_is_ignored()
    {
        var raft = await factory.SeededRaftAsync();
        var owner = raft.Owner.Client;
        await factory.UpdateAsync<CultivationCycle>(raft.CycleId, c => c.TargetYieldKg = 100m);

        var saved = await owner.PostAsync($"/api/rafts/{raft.RaftId}/harvest-lots", await WithConfirmationAsync(owner, new HarvestFields { Cages = "8" }, "true", "late"));

        var lot = (await saved.ReadAsync(HttpStatusCode.Created)).GetProperty("lot");
        Assert.Equal(JsonValueKind.Null, lot.GetProperty("overPlanReason").ValueKind);
    }

    [Fact]
    public async Task A_cycle_with_no_target_has_no_cap()
    {
        var raft = await factory.SeededRaftAsync();

        var lot = await raft.Owner.Client.HarvestLotAsync(raft.RaftId, new HarvestFields { Cages = "100000" });

        Assert.Equal(5_000_000m, lot.Kg("estimatedKg"));
        Assert.Equal(JsonValueKind.Null, lot.GetProperty("overPlanReason").ValueKind);
    }

    [Fact]
    public async Task A_correction_that_raises_the_kg_above_the_target_is_not_asked_again()
    {
        var raft = await factory.SeededRaftAsync();
        var owner = raft.Owner.Client;
        var lot = await owner.HarvestLotAsync(raft.RaftId, new HarvestFields { Cages = "10" }); // 500 kg
        await factory.UpdateAsync<CultivationCycle>(raft.CycleId, c => c.TargetYieldKg = 450m);

        var raised = await factory.SendCorrectionAsync(owner, lot.Id(), new HarvestFields { Cages = "12" }, "recount");

        Assert.Equal(HttpStatusCode.OK, raised.StatusCode);
    }

    [Fact]
    public async Task Finish_harvest_marks_a_lane_being_harvested_without_a_lot_edit_is_idempotent_and_audited()
    {
        var raft = await factory.SeededRaftAsync();
        var owner = raft.Owner.Client;
        var lot = await owner.HarvestLotAsync(raft.RaftId, HarvestFields.OfLanes(1, 2));

        var first = await (await owner.PostAsync($"/api/rafts/{raft.RaftId}/lanes/1/finish-harvest", null)).ReadAsync(HttpStatusCode.OK);

        Assert.Equal((1, "Harvested", true, lot.Text("lotCode")), (first.GetProperty("laneNumber").GetInt32(), first.Text("state"), first.GetProperty("changed").GetBoolean(), first.Text("lotCode")));
        Assert.False(first.GetProperty("everySeededLaneHarvested").GetBoolean());
        var screen = await owner.HarvestScreenAsync(raft.RaftId);
        Assert.Equal(["Harvested", "BeingHarvested", "Growing"], [screen.HarvestStateOf(1), screen.HarvestStateOf(2), screen.HarvestStateOf(3)]);
        Assert.Equal([1], (await owner.GetHarvestLotAsync(lot.Id())).Numbers("finishedLaneNumbers"));

        var second = await (await owner.PostAsync($"/api/rafts/{raft.RaftId}/lanes/1/finish-harvest", null)).ReadAsync(HttpStatusCode.OK);
        Assert.False(second.GetProperty("changed").GetBoolean());
        Assert.Equal(1, await factory.QueryAsync(db => Task.FromResult(db.AuditEvents.Count(a => a.Action == "HarvestLot.LaneFinished" && a.RecordId == lot.Id()))));
    }

    [Fact]
    public async Task Finishing_the_last_seeded_lane_tells_the_cycle_can_be_closed()
    {
        var raft = await factory.SeededRaftAsync(1);
        var owner = raft.Owner.Client;
        await owner.HarvestLotAsync(raft.RaftId, HarvestFields.OfLanes(1));

        var done = await (await owner.PostAsync($"/api/rafts/{raft.RaftId}/lanes/1/finish-harvest", null)).ReadAsync(HttpStatusCode.OK);

        Assert.True(done.GetProperty("everySeededLaneHarvested").GetBoolean());
    }

    [Fact]
    public async Task Finish_harvest_is_refused_for_a_lane_with_no_lot_an_unknown_lane_a_stranger_and_a_raft_with_no_cycle()
    {
        var raft = await factory.SeededRaftAsync();
        var owner = raft.Owner.Client;
        await owner.HarvestLotAsync(raft.RaftId, HarvestFields.OfLanes(1));

        var growing = await (await owner.PostAsync($"/api/rafts/{raft.RaftId}/lanes/2/finish-harvest", null)).ReadAsync(HttpStatusCode.Conflict);
        Assert.Equal("MSG-GEN-09", growing.Text("code"));
        // The detail is in Vietnamese (QA of 13/11/2026), naming the lane and the raft.
        Assert.StartsWith("Làn 2 của bè ", growing.Text("detail"));
        Assert.Equal(HttpStatusCode.NotFound, (await owner.PostAsync($"/api/rafts/{raft.RaftId}/lanes/99/finish-harvest", null)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await owner.PostAsync($"/api/rafts/{Guid.NewGuid()}/lanes/1/finish-harvest", null)).StatusCode);

        var otherZone = await (await factory.OwnerAsync()).Client.CreateZoneAsync();
        var stranger = await factory.StaffAsync(await factory.ZoneEntityAsync(otherZone.Id()));
        Assert.Equal(HttpStatusCode.Forbidden, (await stranger.Client.PostAsync($"/api/rafts/{raft.RaftId}/lanes/1/finish-harvest", null)).StatusCode);

        await factory.CloseCycleAsync(raft.CycleId);
        var closed = await (await owner.PostAsync($"/api/rafts/{raft.RaftId}/lanes/1/finish-harvest", null)).ReadAsync(HttpStatusCode.Conflict);
        Assert.Equal("MSG-GEN-09", closed.Text("code"));
        Assert.Contains("chưa có chu kỳ đang mở", closed.Text("detail"));
    }

    [Fact]
    public async Task The_screen_gives_the_facts_of_each_lane_and_the_cycle_progress_against_the_target()
    {
        var raft = await factory.SeededRaftAsync(1, 2);
        var owner = raft.Owner.Client;
        await factory.UpdateAsync<CultivationCycle>(raft.CycleId, c => c.TargetYieldKg = 1000m);
        await owner.HarvestLotAsync(raft.RaftId, new HarvestFields { Lanes = ["1", "2"], Cages = "4" }); // 200 kg over two lanes
        await owner.HarvestLotAsync(raft.RaftId, new HarvestFields { Lanes = ["1"], Cages = "2" }); // 100 kg
        await owner.PostAsync($"/api/rafts/{raft.RaftId}/lanes/2/finish-harvest", null);

        var screen = await owner.HarvestScreenAsync(raft.RaftId);

        Assert.Equal((6m, 300m, 1000m, 700m), (screen.Kg("harvestedCages"), screen.Kg("harvestedKg"), screen.Kg("targetYieldKg"), screen.Kg("remainingKg")));
        var one = screen.GetProperty("lanes")[0];
        Assert.Equal((2, 4m, true, true, "BeingHarvested"), (one.GetProperty("lotCount").GetInt32(), one.Kg("harvestedCages"), one.GetProperty("canHarvest").GetBoolean(), one.GetProperty("canFinish").GetBoolean(), one.Text("state")));
        Assert.Equal(Today.ToString("yyyy-MM-dd"), one.Text("lastHarvestDate"));
        Assert.Single(one.GetProperty("seedLotCodes").EnumerateArray());
        var two = screen.GetProperty("lanes")[1];
        Assert.Equal(("AlreadyHarvested", false), (two.Text("reasonCode"), two.GetProperty("canHarvest").GetBoolean()));
        var three = screen.GetProperty("lanes")[2];
        Assert.Equal(("NotSeeded", 0), (three.Text("reasonCode"), three.GetProperty("lotCount").GetInt32()));
    }

    [Fact]
    public async Task Finishing_a_seeding_with_no_seeded_lane_needs_confirm_empty()
    {
        var setup = await factory.CycleAsync();
        var owner = setup.Owner.Client;
        var list = await owner.SeedingListAsync(setup.CycleId);

        var asked = await (await owner.PostSeedingCompleteAsync(setup.CycleId, list.Text("cycleRowVersion"))).ReadAsync(HttpStatusCode.Conflict);

        Assert.Equal(("MSG-CYCLE-01", "confirmEmpty"), (asked.Text("code"), asked.Text("confirmFlag")));
        var preview = await (await owner.CompletionPreviewAsync(setup.CycleId)).ReadAsync(HttpStatusCode.OK);
        Assert.Equal("MSG-CYCLE-01", preview.Text("messageCode"));
        var done = await owner.PostAsJsonAsync($"/api/cultivation-cycles/{setup.CycleId}/seeding-complete", new { rowVersion = list.Text("cycleRowVersion"), confirmEmpty = true });
        Assert.Equal(HttpStatusCode.OK, done.StatusCode);
    }

    [Fact]
    public async Task A_seeding_with_seeded_lanes_completes_without_the_flag()
    {
        var raft = await factory.SeededRaftAsync(1);

        var done = await raft.Owner.Client.MarkSeedingCompleteAsync(raft.CycleId);

        Assert.Equal(1, done.GetProperty("seededCount").GetInt32());
    }

    [Fact]
    public async Task Seed_lots_come_with_the_catalogue_unit_of_the_item()
    {
        var raft = await factory.SeededRaftAsync(1);

        var lots = await raft.Owner.Client.SeedLotsAsync(raft.RaftId);

        Assert.All(lots.GetProperty("lots").EnumerateArray(), l => Assert.Equal("kg", l.Text("unit")));
    }

    [Theory]
    [InlineData(0, 0)]
    [InlineData(106.6, 10.8)]
    public async Task A_zone_pin_of_zero_zero_or_swapped_is_refused_with_a_hint(double latitude, double longitude)
    {
        var owner = (await factory.OwnerAsync()).Client;
        var body = NewZoneBody() with { Latitude = (decimal)latitude, Longitude = (decimal)longitude };

        var answer = await (await owner.PostAsJsonAsync("/api/zones", body)).ReadAsync(HttpStatusCode.BadRequest);

        Assert.Equal(("MSG-GEN-01", "Latitude"), (answer.Text("code"), answer.Text("field")));
        Assert.Equal(["Latitude", "Longitude"], answer.GetProperty("fields").EnumerateArray().Select(f => f.GetString()));
        Assert.False(string.IsNullOrEmpty(answer.Text("hint")));
    }

    [Fact]
    public async Task A_zone_pin_outside_vietnam_saves_with_a_warning_and_a_raft_far_from_it_too()
    {
        var owner = (await factory.OwnerAsync()).Client;
        var abroad = await (await owner.PostAsJsonAsync("/api/zones", NewZoneBody() with { Latitude = 35m, Longitude = 139m })).ReadAsync(HttpStatusCode.Created);
        Assert.Equal("COORDINATES_OUTSIDE_VIETNAM", Assert.Single(abroad.GetProperty("warnings").EnumerateArray()).Text("code"));

        var zone = await owner.CreateZoneAsync();
        var near = await (await owner.PostAsJsonAsync($"/api/zones/{zone.Id()}/rafts", NewRaftBody())).ReadAsync(HttpStatusCode.Created);
        Assert.Empty(near.GetProperty("warnings").EnumerateArray());
        var far = await (await owner.PostAsJsonAsync($"/api/zones/{zone.Id()}/rafts", NewRaftBody() with { Latitude = 21.15m })).ReadAsync(HttpStatusCode.Created);
        Assert.Equal("RAFT_FAR_FROM_ZONE", Assert.Single(far.GetProperty("warnings").EnumerateArray()).Text("code"));
        var none = await (await owner.PostAsJsonAsync($"/api/zones/{zone.Id()}/rafts", NewRaftBody() with { Latitude = null, Longitude = null })).ReadAsync(HttpStatusCode.Created);
        Assert.Empty(none.GetProperty("warnings").EnumerateArray());
    }

    [Fact]
    public async Task The_yield_report_returns_cages_target_remaining_seed_and_available_kg_per_cycle()
    {
        var raft = await factory.SeededRaftAsync(1);
        var owner = raft.Owner.Client;
        await factory.UpdateAsync<CultivationCycle>(raft.CycleId, c => c.TargetYieldKg = 1000m);
        await owner.HarvestLotAsync(raft.RaftId, new HarvestFields { Cages = "4.5" }); // 225 kg

        var report = await owner.GetFromJsonAsync<JsonElement>($"/api/harvest-yield?groupBy=cycle&zoneIds={raft.ZoneId}");

        var group = report.GetProperty("groups").EnumerateArray().Single(g => g.GetProperty("cycleId").GetGuid() == raft.CycleId);
        Assert.Equal((4.5m, 225m, 1000m, 775m, 225m), (group.Kg("harvestedCages"), group.Kg("harvestedKg"), group.Kg("targetYieldKg"), group.Kg("remainingKg"), group.Kg("availableKg")));
        Assert.Equal("kg", group.Text("seedUnit"));
        Assert.True(group.Kg("seedQuantity") > 0);
        Assert.Equal(0m, group.Kg("soldKg"));
        Assert.Equal(4.5m, group.GetProperty("lots")[0].Kg("cageCount"));
        Assert.Equal(4.5m, report.GetProperty("totals").Kg("harvestedCages"));
    }

    [Fact]
    public async Task The_cycle_summary_close_preview_and_close_agree_and_a_zone_manager_needs_the_owner()
    {
        var raft = await factory.SeededRaftAsync(1);
        var owner = raft.Owner.Client;
        await factory.UpdateAsync<CultivationCycle>(raft.CycleId, c => c.TargetYieldKg = 1000m);
        await owner.HarvestLotAsync(raft.RaftId, new HarvestFields { Cages = "2.5" });
        var manager = await factory.ManagerAsync(raft.ZoneId);

        var summary = await manager.Client.GetFromJsonAsync<JsonElement>($"/api/cultivation-cycles/{raft.CycleId}/summary");
        var preview = await manager.Client.GetFromJsonAsync<JsonElement>($"/api/cultivation-cycles/{raft.CycleId}/close/preview");
        var ownerPreview = await owner.GetFromJsonAsync<JsonElement>($"/api/cultivation-cycles/{raft.CycleId}/close/preview");

        Assert.Equal((2.5m, 125m, 875m), (summary.Kg("harvestedCages"), summary.Kg("kgHarvested"), summary.Kg("remainingKg")));
        Assert.Equal(summary.GetRawText(), preview.GetProperty("summary").GetRawText());
        Assert.True(preview.GetProperty("isEarly").GetBoolean());
        Assert.True(preview.GetProperty("needsOwnerApproval").GetBoolean());
        Assert.False(ownerPreview.GetProperty("needsOwnerApproval").GetBoolean());
        var closed = await owner.PostAsJsonAsync($"/api/cultivation-cycles/{raft.CycleId}/close", new { assessment = "Done", rowVersion = ownerPreview.Text("cycleRowVersion") });
        var closedBody = await closed.ReadAsync(HttpStatusCode.OK);
        Assert.Equal(125m, closedBody.GetProperty("summary").Kg("kgHarvested"));
    }

    private static async Task<MultipartFormDataContent> WithConfirmationAsync(HttpClient client, HarvestFields fields, string confirm, string? reason)
    {
        // A new lot needs 1 to 3 on-site photos (owner decision of 12/11/2026).
        var form = (fields.Photos.Count == 0 ? fields with { Photos = [FarmingTestKit.Jpeg] } : fields).For(await HarvestOrderKit.DefaultOrderAsync(client)).ToForm();
        form.Add(new StringContent(confirm), "confirmOverPlan");
        if (reason is not null)
        {
            form.Add(new StringContent(reason), "overPlanReason");
        }

        return form;
    }

}
