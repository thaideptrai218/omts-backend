using System.Net;
using System.Net.Http.Json;
using OMTS.Api.IntegrationTests.Cultivation;
using OMTS.Api.IntegrationTests.Farming;
using OMTS.Api.IntegrationTests.Infrastructure;
using OMTS.Domain.Constants;
using OMTS.Domain.Entities.Farming;
using static OMTS.Api.IntegrationTests.Farming.FarmingTestKit;

namespace OMTS.Api.IntegrationTests.Harvest;

/// <summary>HARVEST-01 step 6 and PRE-2: the fields, the lanes and their states, a raft with no Open cycle, and the screen.</summary>
public class HarvestLotRecordTests(AuthApiFactory factory) : IClassFixture<AuthApiFactory>
{
    public static TheoryData<string, HarvestFields> InvalidFields => new()
    {
        { "Lanes", new HarvestFields { Lanes = [] } },
        { "Lanes", new HarvestFields { Lanes = ["1", "1"] } },
        { "Lanes", new HarvestFields { Lanes = ["0"] } },
        { "Lanes", new HarvestFields { Lanes = ["-1"] } },
        { "Lanes", new HarvestFields { Lanes = ["99"] } },
        { "Lanes", new HarvestFields { Lanes = ["x"] } },
        { "Lanes", new HarvestFields { Lanes = ["1", ""] } },
        { "Lanes", new HarvestFields { Lanes = ["99999999999"] } },
        { "Finished lanes", new HarvestFields { Lanes = ["1"], Finished = ["2"] } },
        { "Finished lanes", new HarvestFields { Lanes = ["1"], Finished = ["1", "1"] } },
        { "Finished lanes", new HarvestFields { Lanes = ["1"], Finished = ["x"] } },
        { "Number of cages", new HarvestFields { Cages = null } },
        { "Number of cages", new HarvestFields { Cages = "0" } },
        { "Number of cages", new HarvestFields { Cages = "-8" } },
        { "Number of cages", new HarvestFields { Cages = "many" } },
        { "Number of cages", new HarvestFields { Cages = "99999999999" } },
        { "Estimated kg", new HarvestFields { EstimatedKg = "0" } },
        { "Estimated kg", new HarvestFields { EstimatedKg = "-1" } },
        { "Estimated kg", new HarvestFields { EstimatedKg = "1.234" } },
        { "Estimated kg", new HarvestFields { EstimatedKg = "1000000000.01" } },
        { "Estimated kg", new HarvestFields { EstimatedKg = "about 400" } },
        { "Number of cages", new HarvestFields { Cages = "2147483647" } },
        { "Photos", new HarvestFields { Photos = [FarmingTestKit.TextPretendingToBeAFile] } },
    };

    [Theory]
    [MemberData(nameof(InvalidFields))]
    public async Task An_invalid_field_is_named_and_saves_nothing(string field, HarvestFields fields)
    {
        var raft = await factory.SeededRaftAsync();

        var answer = await (await raft.Owner.Client.PostHarvestAsync(raft.RaftId, fields)).ReadAsync(HttpStatusCode.BadRequest);

        Assert.Equal(("MSG-GEN-01", field), (answer.Text("code"), answer.Text("field")));
        Assert.Equal(0, await factory.LotCountAsync(raft.RaftId));
    }

    [Fact]
    public async Task Every_invalid_field_is_listed()
    {
        var raft = await factory.SeededRaftAsync();

        var answer = await raft.Owner.Client.PostHarvestAsync(raft.RaftId, new HarvestFields { Lanes = [], Finished = ["2"], Cages = "0", EstimatedKg = "-1" });

        Assert.Equal(["Lanes", "Finished lanes", "Number of cages", "Estimated kg"], await answer.InvalidFieldsAsync());
    }

    [Theory]
    [InlineData("8", "0.01", 0.01)]
    [InlineData("100000", "50000000", 50000000)]
    [InlineData("8", "400.50", 400.5)]
    [InlineData("8", "", 400)]
    public async Task The_limits_of_the_estimate_are_saved(string cages, string sent, decimal saved)
    {
        var raft = await factory.SeededRaftAsync();

        var lot = await raft.Owner.Client.HarvestLotAsync(raft.RaftId, new HarvestFields { Cages = cages, EstimatedKg = sent });

        Assert.Equal(saved, lot.Kg("estimatedKg"));
    }

    // BR-HARVEST-01: only lanes In Use that are Growing or Being harvested.
    [Fact]
    public async Task A_lane_with_no_seed_is_refused_and_named_and_nothing_is_saved()
    {
        var raft = await factory.SeededRaftAsync(1, 2);

        var answer = await (await raft.Owner.Client.PostHarvestAsync(raft.RaftId, HarvestFields.OfLanes(2, 3, 4))).ReadAsync(HttpStatusCode.BadRequest);

        Assert.Equal(("Lanes", "Lanes 3, 4 cannot be harvested now."), (answer.Text("field"), answer.Text("hint")));
        Assert.Equal(0, await factory.LotCountAsync(raft.RaftId));
    }

    [Fact]
    public async Task A_removed_lane_is_refused()
    {
        var raft = await factory.SeededRaftAsync(1);
        await factory.UpdateAsync<Lane>(await factory.QueryAsync(db => Task.FromResult(db.Lanes.Single(l => l.RaftId == raft.RaftId && l.LaneNumber == 5).Id)), l => l.StatusId = StatusIds.Lane.Removed);

        var answer = await (await raft.Owner.Client.PostHarvestAsync(raft.RaftId, HarvestFields.OfLanes(5))).ReadAsync(HttpStatusCode.BadRequest);

        Assert.Equal("Lanes", answer.Text("field"));
    }

    [Fact]
    public async Task A_lane_being_harvested_stays_choosable_and_several_lanes_finish_in_one_lot()
    {
        var raft = await factory.SeededRaftAsync();
        var owner = raft.Owner.Client;
        await owner.HarvestLotAsync(raft.RaftId, HarvestFields.OfLanes(1, 2));

        var second = await owner.HarvestLotAsync(raft.RaftId, HarvestFields.OfLanes(1, 2, 3).FinishedLanes(1, 3));

        Assert.Equal([1, 2, 3], second.Numbers("laneNumbers"));
        Assert.Equal([1, 3], second.Numbers("finishedLaneNumbers"));
        var screen = await owner.HarvestScreenAsync(raft.RaftId);
        Assert.Equal(["Harvested", "BeingHarvested", "Harvested", "Growing", "Growing"], Enumerable.Range(1, 5).Select(lane => screen.HarvestStateOf(lane)));
    }

    [Fact]
    public async Task With_no_cycle_or_a_closed_cycle_or_a_discontinued_raft_or_zone_the_answer_is_msg_gen_09()
    {
        var owner = await factory.OwnerAsync();
        var zoneId = (await owner.Client.CreateZoneAsync()).Id();
        var noCycle = await owner.Client.CreateRaftAsync(zoneId);
        var refused = await (await owner.Client.PostHarvestAsync(noCycle.Id())).ReadAsync(HttpStatusCode.Conflict);
        Assert.Equal(("MSG-GEN-09", $"Raft {noCycle.Text("code")} is without an Open cycle and cannot be used for this action."), (refused.Text("code"), refused.Text("detail")));

        var closed = await factory.SeededRaftAsync();
        await factory.CloseCycleAsync(closed.CycleId);
        Assert.Equal("MSG-GEN-09", await (await closed.Owner.Client.PostHarvestAsync(closed.RaftId)).ErrorCodeAsync());
        var closedScreen = await closed.Owner.Client.HarvestScreenAsync(closed.RaftId);
        Assert.False(closedScreen.GetProperty("canRecordHarvest").GetBoolean());
        Assert.Equal(System.Text.Json.JsonValueKind.Null, closedScreen.GetProperty("cycle").ValueKind);

        var discontinued = await factory.SeededRaftAsync();
        await factory.DiscontinueRaftAsync(discontinued.RaftId);
        Assert.Equal("MSG-GEN-09", await (await discontinued.Owner.Client.PostHarvestAsync(discontinued.RaftId)).ErrorCodeAsync());

        var inZone = await factory.SeededRaftAsync();
        await factory.UpdateAsync<FarmingZone>(inZone.ZoneId, z => z.StatusId = StatusIds.Usage.Discontinued);
        Assert.Equal("MSG-GEN-09", await (await inZone.Owner.Client.PostHarvestAsync(inZone.RaftId)).ErrorCodeAsync());
        Assert.False((await inZone.Owner.Client.HarvestScreenAsync(inZone.RaftId)).GetProperty("canRecordHarvest").GetBoolean());
    }

    // A refused state comes before the fields, so a form that cannot be saved is not asked to be corrected.
    [Fact]
    public async Task A_refused_raft_answers_before_the_fields_and_an_unknown_raft_is_404()
    {
        var closed = await factory.SeededRaftAsync();
        await factory.CloseCycleAsync(closed.CycleId);

        Assert.Equal("MSG-GEN-09", await (await closed.Owner.Client.PostHarvestAsync(closed.RaftId, new HarvestFields { Cages = "0" })).ErrorCodeAsync());
        Assert.Equal(HttpStatusCode.NotFound, (await closed.Owner.Client.PostHarvestAsync(Guid.NewGuid())).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await closed.Owner.Client.GetAsync($"/api/rafts/{Guid.NewGuid()}/harvest-lots")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await closed.Owner.Client.GetAsync($"/api/harvest-lots/{Guid.NewGuid()}")).StatusCode);
    }

    [Fact]
    public async Task Json_is_a_bare_415()
    {
        var raft = await factory.SeededRaftAsync();

        var answer = await raft.Owner.Client.PostAsJsonAsync($"/api/rafts/{raft.RaftId}/harvest-lots", new { laneNumbers = new[] { 1 }, cageCount = 8 });

        Assert.Equal(HttpStatusCode.UnsupportedMediaType, answer.StatusCode);
    }

    // SCR-HARVEST-01: Record harvest only while a lane is Growing or Being harvested; lots for sale first, then the latest; no lot of another cycle or raft.
    [Fact]
    public async Task The_screen_shows_lanes_setting_commands_and_lots_for_sale_first()
    {
        var raft = await factory.SeededRaftAsync(1, 2);
        var owner = raft.Owner.Client;

        var empty = await owner.HarvestScreenAsync(raft.RaftId);
        Assert.True(empty.GetProperty("canRecordHarvest").GetBoolean());
        Assert.Equal(raft.CycleId, empty.GetProperty("cycle").Id());
        Assert.Equal(50m, empty.Kg("averageKgPerCage"));
        Assert.Empty(empty.GetProperty("lots").EnumerateArray());
        Assert.Equal("NotSeeded", empty.HarvestStateOf(3));
        Assert.False(empty.CanHarvest(3));

        var first = await owner.HarvestLotAsync(raft.RaftId, HarvestFields.OfLanes(1));
        var second = await owner.HarvestLotAsync(raft.RaftId, HarvestFields.OfLanes(2));
        await factory.UpdateAsync<OMTS.Domain.Entities.Harvest.HarvestLot>(second.Id(), l => l.StatusId = StatusIds.HarvestLot.Closed);

        var screen = await owner.HarvestScreenAsync(raft.RaftId);

        Assert.Equal([first.Id(), second.Id()], screen.GetProperty("lots").EnumerateArray().Select(l => l.Id()));
        var row = screen.GetProperty("lots")[0];
        Assert.Equal((0m, 400m), (row.Kg("invoicedKg"), row.Kg("remainingKg")));
        Assert.Equal("NotPublished", row.Text("originState"));
        Assert.Equal([1], row.Numbers("laneNumbers"));

        // Every seeded lane Harvested: nothing is left to record, though lanes have no seed.
        await owner.HarvestLotAsync(raft.RaftId, HarvestFields.OfLanes(1, 2).FinishedLanes(1, 2));
        Assert.False((await owner.HarvestScreenAsync(raft.RaftId)).GetProperty("canRecordHarvest").GetBoolean());
    }

    [Fact]
    public async Task The_detail_holds_the_figures_of_br_harvest_07_with_no_sales_yet_and_the_recorder()
    {
        var raft = await factory.SeededRaftAsync();
        var manager = await factory.ManagerAsync(raft.ZoneId);

        var saved = await manager.Client.RecordHarvestAsync(raft.RaftId, new HarvestFields { Cages = "2" });

        Assert.False(saved.GetProperty("everySeededLaneHarvested").GetBoolean());
        Assert.Equal(("Harvest lot saved.", "MSG-GEN-05"), (saved.Text("message"), saved.Text("messageCode")));
        var lot = await manager.Client.GetHarvestLotAsync(saved.GetProperty("lot").Id());
        var figures = lot.GetProperty("figures");
        Assert.Equal((0m, 0m, 0m, 100m, 0m, 100m), (figures.Kg("invoicedKg"), figures.Kg("returnedKg"), figures.Kg("soldKg"), figures.Kg("remainingKg"), figures.Kg("lostKg"), figures.Kg("differenceKg")));
        Assert.True(figures.GetProperty("notFinal").GetBoolean());
        Assert.Equal((manager.Account.Id, manager.Account.DisplayName), (lot.GetProperty("recordedByAccountId").GetGuid(), lot.Text("recordedByName")));
        Assert.Equal((raft.RaftId, raft.CycleId, raft.ZoneId), (lot.GetProperty("raftId").GetGuid(), lot.GetProperty("cycleId").GetGuid(), lot.GetProperty("zoneId").GetGuid()));
    }

    // CYCLE-01.2: a cycle with lots has records, and they are counted in the refusal.
    [Fact]
    public async Task A_cycle_with_a_lot_cannot_be_cancelled_and_the_refusal_counts_it()
    {
        var raft = await factory.SeededRaftAsync();
        var owner = raft.Owner.Client;
        await owner.HarvestLotAsync(raft.RaftId);
        var cycle = await owner.GetFromJsonAsync<System.Text.Json.JsonElement>($"/api/cultivation-cycles/{raft.CycleId}");

        var refused = await (await owner.CancelCycleAsync(raft.CycleId, cycle.Text("rowVersion"))).ReadAsync(HttpStatusCode.Conflict);

        Assert.Equal("MSG-GEN-06", refused.Text("code"));
        Assert.Contains("2 records", refused.Text("detail"));
    }
}
