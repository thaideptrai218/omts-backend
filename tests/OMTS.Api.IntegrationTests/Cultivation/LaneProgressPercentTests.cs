using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using OMTS.Api.IntegrationTests.Farming;
using OMTS.Api.IntegrationTests.Harvest;
using OMTS.Api.IntegrationTests.Infrastructure;
using OMTS.Domain.Entities.Cultivation;

namespace OMTS.Api.IntegrationTests.Cultivation;

/// <summary>
/// Owner decision of 12/11/2026: the share of a lane a seeding session hangs and a harvest lot takes (default 100 when seeding), the shares of a lane adding up to at most 100 in a cycle, the state following from
/// them, "finish harvest" bringing the harvest to 100, and the manual change of the percentages (<c>PUT /api/rafts/{raftId}/lanes/{n}/progress</c>) with its history.
/// </summary>
public class LaneProgressPercentTests(AuthApiFactory factory) : IClassFixture<AuthApiFactory>
{
    private static JsonElement LaneOf(JsonElement screen, int number) =>
        screen.GetProperty("lanes").EnumerateArray().Single(l => l.GetProperty("laneNumber").GetInt32() == number);

    private static SeedingBody Share(SeedLotMade lot, int lane, decimal percent) =>
        lot.Session([lane], quantity: 5m) with { LanePercents = new Dictionary<int, decimal> { [lane] = percent } };

    private static string Percent(JsonElement lane, string property) => lane.GetProperty(property).ValueKind == JsonValueKind.Null ? "null" : lane.GetProperty(property).GetDecimal().ToString("0.##");

    [Fact]
    public async Task Seeding_60_percent_then_40_fills_the_lane_and_ten_more_is_refused_naming_the_lane_and_what_is_left()
    {
        var setup = await factory.CycleAsync();
        var owner = setup.Owner.Client;
        var lot = await owner.SeedLotAsync(1000m);

        var first = await owner.SeedingRecordAsync(setup.CycleId, Share(lot, 1, 60m));
        Assert.Equal([60m], first.GetProperty("lanePercents").EnumerateArray().Select(p => p.GetProperty("percent").GetDecimal()));
        var growing = LaneOf(await owner.SeedingListAsync(setup.CycleId), 1);
        Assert.Equal(("Growing", "60"), (growing.Text("state"), Percent(growing, "seededPercent")));

        await owner.SeedingRecordAsync(setup.CycleId, Share(lot, 1, 40m));
        Assert.Equal("100", Percent(LaneOf(await owner.SeedingListAsync(setup.CycleId), 1), "seededPercent"));

        var refused = await (await owner.PostSeedingAsync(setup.CycleId, Share(lot, 1, 10m))).ReadAsync(HttpStatusCode.BadRequest);
        Assert.Equal(("MSG-GEN-01", "LanePercent", 1, 0m), (refused.Text("code"), refused.Text("field"), refused.GetProperty("laneNumber").GetInt32(), refused.GetProperty("remainingPercent").GetDecimal()));
        Assert.Equal(2, await factory.QueryAsync(db => db.SeedingRecords.CountAsync(r => r.CycleId == setup.CycleId)));
    }

    [Fact]
    public async Task A_session_that_names_no_percent_takes_the_whole_lane_and_a_second_one_on_it_is_refused_with_nothing_left()
    {
        var setup = await factory.CycleAsync();
        var owner = setup.Owner.Client;
        var lot = await owner.SeedLotAsync(1000m);
        await owner.SeedingRecordAsync(setup.CycleId, lot.Session([1, 2]));

        var refused = await (await owner.PostSeedingAsync(setup.CycleId, lot.Session([2, 3]))).ReadAsync(HttpStatusCode.BadRequest);

        Assert.Equal(("LanePercent", 2, 0m), (refused.Text("field"), refused.GetProperty("laneNumber").GetInt32(), refused.GetProperty("remainingPercent").GetDecimal()));

        var third = await owner.SeedingRecordAsync(setup.CycleId, Share(lot, 3, 60m));
        Assert.Equal(60m, third.GetProperty("lanePercents")[0].GetProperty("percent").GetDecimal());
    }

    [Theory]
    [InlineData("{\"1\":0}")]
    [InlineData("{\"1\":101}")]
    [InlineData("{\"1\":12.345}")]
    [InlineData("{\"7\":50}")]
    [InlineData("not json")]
    public async Task A_percent_that_is_not_above_0_and_at_most_100_with_two_decimals_or_names_a_lane_not_in_the_session_is_refused(string lanePercents)
    {
        var setup = await factory.CycleAsync();
        var owner = setup.Owner.Client;
        var lot = await owner.SeedLotAsync(1000m);
        var form = StepFormKit.FormOf(lot.Session([1]), photos: 1);
        form.Add(new StringContent(lanePercents), "lanePercents");

        var refused = await (await owner.PostAsync($"/api/cultivation-cycles/{setup.CycleId}/seeding-records", form)).ReadAsync(HttpStatusCode.BadRequest);

        Assert.Equal("LanePercent", refused.Text("field"));
        Assert.False(refused.TryGetProperty("remainingPercent", out _));
    }

    [Fact]
    public async Task A_correction_keeps_the_share_of_a_lane_it_does_not_name_and_cannot_push_one_above_100()
    {
        var setup = await factory.CycleAsync();
        var owner = setup.Owner.Client;
        var lot = await owner.SeedLotAsync(1000m);
        await owner.SeedingRecordAsync(setup.CycleId, Share(lot, 1, 60m));
        var second = await owner.SeedingRecordAsync(setup.CycleId, Share(lot, 1, 30m));

        var tooBig = await (await owner.PutSeedingAsync(second.Id(), second.AsCorrection() with { LanePercents = new Dictionary<int, decimal> { [1] = 50m } })).ReadAsync(HttpStatusCode.BadRequest);
        Assert.Equal((40m, "LanePercent"), (tooBig.GetProperty("remainingPercent").GetDecimal(), tooBig.Text("field")));

        var same = await (await owner.PutSeedingAsync(second.Id(), second.AsCorrection() with { Quantity = 6m })).ReadAsync(HttpStatusCode.OK);
        Assert.Equal(30m, same.GetProperty("record").GetProperty("lanePercents")[0].GetProperty("percent").GetDecimal());
    }

    [Fact]
    public async Task A_harvest_takes_a_share_of_the_lane_until_100_and_the_lane_is_harvested_at_100()
    {
        var raft = await factory.SeededRaftAsync(1, 2);
        var owner = raft.Owner.Client;

        var first = await owner.HarvestLotAsync(raft.RaftId, HarvestFields.OfLanes(1) with { LanePercents = new Dictionary<int, decimal> { [1] = 50m } });
        Assert.Equal(50m, first.GetProperty("lanePercents")[0].GetProperty("percent").GetDecimal());
        var half = LaneOf(await owner.HarvestScreenAsync(raft.RaftId), 1);
        Assert.Equal(("BeingHarvested", "50", "100"), (half.Text("state"), Percent(half, "harvestedPercent"), Percent(half, "seededPercent")));

        var refused = await (await owner.PostHarvestAsync(raft.RaftId, HarvestFields.OfLanes(1) with { LanePercents = new Dictionary<int, decimal> { [1] = 60m } })).ReadAsync(HttpStatusCode.BadRequest);
        Assert.Equal(("LanePercent", 1, 50m), (refused.Text("field"), refused.GetProperty("laneNumber").GetInt32(), refused.GetProperty("remainingPercent").GetDecimal()));

        await owner.HarvestLotAsync(raft.RaftId, HarvestFields.OfLanes(1) with { LanePercents = new Dictionary<int, decimal> { [1] = 50m } });
        var whole = LaneOf(await owner.HarvestScreenAsync(raft.RaftId), 1);
        Assert.Equal(("Harvested", "100", false), (whole.Text("state"), Percent(whole, "harvestedPercent"), whole.Flag("canHarvest")));
    }

    [Fact]
    public async Task A_harvest_with_no_share_leaves_it_unknown_and_finishing_the_lane_or_many_lanes_brings_it_to_100()
    {
        var raft = await factory.SeededRaftAsync(1, 2, 3);
        var owner = raft.Owner.Client;
        await owner.HarvestLotAsync(raft.RaftId, HarvestFields.OfLanes(1, 2, 3));
        var unknown = LaneOf(await owner.HarvestScreenAsync(raft.RaftId), 2);
        Assert.Equal(("BeingHarvested", "null"), (unknown.Text("state"), Percent(unknown, "harvestedPercent")));

        (await owner.PostAsync($"/api/rafts/{raft.RaftId}/lanes/1/finish-harvest", null)).EnsureSuccessStatusCode();
        (await owner.PostAsJsonAsync($"/api/rafts/{raft.RaftId}/finish-harvest", new { laneNumbers = new[] { 2, 3 } })).EnsureSuccessStatusCode();

        var screen = await owner.HarvestScreenAsync(raft.RaftId);
        Assert.All([1, 2, 3], n => Assert.Equal(("Harvested", "100"), (LaneOf(screen, n).Text("state"), Percent(LaneOf(screen, n), "harvestedPercent"))));
        Assert.All(await factory.QueryAsync(db => db.HarvestLotLanes.Where(l => l.Lot.RaftId == raft.RaftId).ToListAsync()), l => Assert.Equal(100m, l.HarvestedPercent));
    }

    [Fact]
    public async Task The_percentages_can_be_changed_by_hand_with_the_history_old_to_new_who_when_and_why_and_what_is_recorded_after_adds_to_them()
    {
        var raft = await factory.SeededRaftAsync(1, 2);
        var owner = raft.Owner.Client;
        var version = (await owner.HarvestScreenAsync(raft.RaftId)).GetProperty("cycle").Text("rowVersion");

        var saved = await (await owner.PutAsJsonAsync($"/api/rafts/{raft.RaftId}/lanes/2/progress", new { seededPercent = 60m, reason = "Counted again by eye", rowVersion = version })).ReadAsync(HttpStatusCode.OK);

        Assert.Equal((2, "60", "0", "Growing", true), (saved.GetProperty("laneNumber").GetInt32(), Percent(saved, "seededPercent"), Percent(saved, "harvestedPercent"), saved.Text("state"), saved.Flag("changed")));
        var change = saved.GetProperty("progressHistory")[0];
        Assert.Equal(("Counted again by eye", 100m, 60m), (change.Text("reason"), change.GetProperty("oldSeededPercent").GetDecimal(), change.GetProperty("seededPercent").GetDecimal()));
        Assert.NotNull(saved.Text("lastModifiedByName"));

        var lane = LaneOf(await owner.HarvestScreenAsync(raft.RaftId), 2);
        Assert.Equal("60", Percent(lane, "seededPercent"));
        Assert.Equal(1, lane.GetProperty("progressHistory").GetArrayLength());

        // What is recorded after the change adds to it: 60 and 40 make the lane whole again.
        await owner.SeedingRecordAsync(raft.CycleId, Share(raft.Lot, 2, 40m));
        Assert.Equal("100", Percent(LaneOf(await owner.SeedingListAsync(raft.CycleId), 2), "seededPercent"));

        var harvested = await (await owner.PutAsJsonAsync($"/api/rafts/{raft.RaftId}/lanes/2/progress", new { harvestedPercent = 20m, reason = "A fifth is already out", rowVersion = version })).ReadAsync(HttpStatusCode.OK);
        Assert.Equal(("100", "20", "BeingHarvested", 2), (Percent(harvested, "seededPercent"), Percent(harvested, "harvestedPercent"), harvested.Text("state"), harvested.GetProperty("progressHistory").GetArrayLength()));

        var same = await (await owner.PutAsJsonAsync($"/api/rafts/{raft.RaftId}/lanes/2/progress", new { seededPercent = 100m, harvestedPercent = 20m, reason = "Same", rowVersion = version })).ReadAsync(HttpStatusCode.OK);
        Assert.False(same.Flag("changed"));
        Assert.Equal(2, same.GetProperty("progressHistory").GetArrayLength());
    }

    [Fact]
    public async Task A_change_by_hand_needs_a_percent_in_range_a_reason_and_a_lane_with_seed()
    {
        var raft = await factory.SeededRaftAsync(1);
        var owner = raft.Owner.Client;
        var url = $"/api/rafts/{raft.RaftId}/lanes/1/progress";
        var version = (await owner.HarvestScreenAsync(raft.RaftId)).GetProperty("cycle").Text("rowVersion");

        Assert.Equal(["Seeded percent"], await (await owner.PutAsJsonAsync(url, new { reason = "x", rowVersion = version })).InvalidFieldsAsync());
        Assert.Equal(["Harvested percent"], await (await owner.PutAsJsonAsync(url, new { harvestedPercent = 101m, reason = "x", rowVersion = version })).InvalidFieldsAsync());
        Assert.Equal(["Reason"], await (await owner.PutAsJsonAsync(url, new { seededPercent = 50m, reason = " ", rowVersion = version })).InvalidFieldsAsync());

        var notSeeded = await (await owner.PutAsJsonAsync($"/api/rafts/{raft.RaftId}/lanes/2/progress", new { seededPercent = 50m, reason = "x", rowVersion = version })).ReadAsync(HttpStatusCode.Conflict);
        Assert.Equal(("MSG-GEN-09", "LANE_NOT_SEEDED"), (notSeeded.Text("code"), notSeeded.Text("reasonCode")));
        Assert.Equal(HttpStatusCode.NotFound, (await owner.PutAsJsonAsync($"/api/rafts/{raft.RaftId}/lanes/99/progress", new { seededPercent = 50m, reason = "x", rowVersion = version })).StatusCode);
    }

    [Fact]
    public async Task A_manager_of_the_zone_may_change_it_and_the_owner_is_told_but_a_manager_of_another_zone_may_not()
    {
        var raft = await factory.SeededRaftAsync(1);
        var manager = await factory.ManagerAsync(raft.ZoneId);
        var other = await factory.ManagerAsync((await raft.Owner.Client.CreateZoneAsync()).Id());
        var version = (await raft.Owner.Client.HarvestScreenAsync(raft.RaftId)).GetProperty("cycle").Text("rowVersion");
        var url = $"/api/rafts/{raft.RaftId}/lanes/1/progress";

        Assert.Equal(HttpStatusCode.Forbidden, (await other.Client.PutAsJsonAsync(url, new { seededPercent = 70m, reason = "x", rowVersion = version })).StatusCode);

        await (await manager.Client.PutAsJsonAsync(url, new { seededPercent = 70m, reason = "Recount", rowVersion = version })).ReadAsync(HttpStatusCode.OK);
        Assert.Contains(await factory.NoticesAsync("LANE_PROGRESS_CHANGED", raft.RaftId), n => n.RecipientAccountId == raft.Owner.Account.Id);

    }

    [Fact]
    public async Task The_owner_is_not_told_of_a_change_the_owner_made()
    {
        var raft = await factory.SeededRaftAsync(1);
        var version = (await raft.Owner.Client.HarvestScreenAsync(raft.RaftId)).GetProperty("cycle").Text("rowVersion");

        await (await raft.Owner.Client.PutAsJsonAsync($"/api/rafts/{raft.RaftId}/lanes/1/progress", new { seededPercent = 80m, reason = "Owner recount", rowVersion = version })).ReadAsync(HttpStatusCode.OK);

        Assert.DoesNotContain(await factory.NoticesAsync("LANE_PROGRESS_CHANGED", raft.RaftId), n => n.RecipientAccountId == raft.Owner.Account.Id);
    }

    [Fact]
    public async Task Finishing_a_lane_after_a_manual_change_still_makes_it_harvested()
    {
        var raft = await factory.SeededRaftAsync(1);
        var owner = raft.Owner.Client;
        await owner.HarvestLotAsync(raft.RaftId, HarvestFields.OfLanes(1));
        var version = (await owner.HarvestScreenAsync(raft.RaftId)).GetProperty("cycle").Text("rowVersion");
        await (await owner.PutAsJsonAsync($"/api/rafts/{raft.RaftId}/lanes/1/progress", new { harvestedPercent = 40m, reason = "Recount", rowVersion = version })).ReadAsync(HttpStatusCode.OK);
        Assert.Equal("40", Percent(LaneOf(await owner.HarvestScreenAsync(raft.RaftId), 1), "harvestedPercent"));

        (await owner.PostAsync($"/api/rafts/{raft.RaftId}/lanes/1/finish-harvest", null)).EnsureSuccessStatusCode();

        var lane = LaneOf(await owner.HarvestScreenAsync(raft.RaftId), 1);
        Assert.Equal(("Harvested", "100"), (lane.Text("state"), Percent(lane, "harvestedPercent")));
        Assert.Equal(2, lane.GetProperty("progressHistory").GetArrayLength());
    }
}
