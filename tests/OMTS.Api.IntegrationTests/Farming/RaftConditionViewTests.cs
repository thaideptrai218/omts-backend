using System.Net.Http.Json;
using System.Net;
using System.Text.Json;
using OMTS.Api.IntegrationTests.Infrastructure;
using OMTS.Domain.Enums;
using static OMTS.Api.IntegrationTests.Farming.FarmingTestKit;

namespace OMTS.Api.IntegrationTests.Farming;

/// <summary>
/// FARM-04 lane segment condition view through the API. Segments are inserted directly, as the CULT and
/// HARV modules that record them do not exist yet.
/// </summary>
public class RaftConditionViewTests(AuthApiFactory factory) : IClassFixture<AuthApiFactory>
{
    // Done when: seeded 0–40 m, harvested 0–10 m, written off 30–40 m → Harvested / Growing / Written off (/ Not seeded on a longer lane).
    [Fact]
    public async Task Portions_follow_the_recorded_segments_of_the_cycle_open_on_the_date()
    {
        var owner = await factory.OwnerAsync();
        var zoneId = (await owner.Client.CreateZoneAsync()).Id();
        var raft = await owner.Client.CreateRaftAsync(zoneId, NewRaftBody(lanes: 2) with
        {
            Lanes = [new(1, 40, "A", "B"), new(2, 50, "A", "B")],
        });
        var cycle = await factory.AddCycleAsync(raft.Id(), DateTimeOffset.UtcNow.AddDays(-20));
        var seeded = await factory.AddSegmentAsync(cycle.Id, raft.LaneId(1), LaneSegmentType.Seeded, 0, 40, Today.AddDays(-15), owner.Account.Id);
        var harvested = await factory.AddSegmentAsync(cycle.Id, raft.LaneId(1), LaneSegmentType.Harvested, 0, 10, Today.AddDays(-2), owner.Account.Id);
        var writtenOff = await factory.AddSegmentAsync(cycle.Id, raft.LaneId(1), LaneSegmentType.WrittenOff, 30, 40, Today.AddDays(-1), owner.Account.Id, "Storm");
        var planned = await factory.AddSegmentAsync(cycle.Id, raft.LaneId(1), LaneSegmentType.PlannedHarvest, 10, 30, Today.AddDays(-1), owner.Account.Id);
        // Recorded for a later date: not part of today's map.
        await factory.AddSegmentAsync(cycle.Id, raft.LaneId(1), LaneSegmentType.Harvested, 10, 30, Today.AddDays(1), owner.Account.Id);
        await factory.AddSegmentAsync(cycle.Id, raft.LaneId(2), LaneSegmentType.Seeded, 0, 40, Today.AddDays(-15), owner.Account.Id);
        await factory.AddSegmentAsync(cycle.Id, raft.LaneId(2), LaneSegmentType.Harvested, 0, 10, Today.AddDays(-2), owner.Account.Id);
        await factory.AddSegmentAsync(cycle.Id, raft.LaneId(2), LaneSegmentType.WrittenOff, 30, 40, Today.AddDays(-1), owner.Account.Id);

        var view = await owner.Client.GetFromJsonAsync<JsonElement>($"/api/rafts/{raft.Id()}/condition");

        Assert.Equal(cycle.Id, view.GetProperty("cycleId").GetGuid());
        var lane1 = view.GetProperty("lanes")[0];
        Assert.Equal([(0m, 10m, "Harvested"), (10m, 30m, "Growing"), (30m, 40m, "WrittenOff")], Portions(lane1));
        Assert.Equal("PartiallyHarvested", lane1.Text("summary"));
        Assert.Equal([planned.Id], lane1.GetProperty("plannedHarvests").EnumerateArray().Select(s => s.Id()));
        Assert.Equal([(0m, 10m, "Harvested"), (10m, 30m, "Growing"), (30m, 40m, "WrittenOff"), (40m, 50m, "NotSeeded")], Portions(view.GetProperty("lanes")[1]));

        // Each portion lists the segments behind it, with the recorder's name and the write-off reason.
        var harvestedSources = lane1.GetProperty("portions")[0].GetProperty("segments").EnumerateArray().ToList();
        Assert.Equal(new[] { seeded.Id, harvested.Id }.Order(), harvestedSources.Select(s => s.Id()).Order());
        Assert.All(harvestedSources, s => Assert.Equal(owner.Account.DisplayName, s.Text("recordedBy")));
        var writeOff = lane1.GetProperty("portions")[2].GetProperty("segments").EnumerateArray().Single(s => s.Id() == writtenOff.Id);
        Assert.Equal("Storm", writeOff.Text("writeOffReason"));
        Assert.Equal("WrittenOff", writeOff.Text("type"));
        Assert.DoesNotContain("kg", view.GetRawText(), StringComparison.OrdinalIgnoreCase);

        // Earlier in the cycle only the seeding was recorded.
        var early = await owner.Client.GetFromJsonAsync<JsonElement>($"/api/rafts/{raft.Id()}/condition?date={Today.AddDays(-10):yyyy-MM-dd}");
        Assert.Equal([(0m, 40m, "Growing")], Portions(early.GetProperty("lanes")[0]));
        Assert.Equal("Growing", early.GetProperty("lanes")[0].Text("summary"));
        Assert.Empty(early.GetProperty("lanes")[0].GetProperty("plannedHarvests").EnumerateArray());

        // Before the cycle opened: no cycle, every lane is one Not seeded portion.
        var before = await owner.Client.GetFromJsonAsync<JsonElement>($"/api/rafts/{raft.Id()}/condition?date={Today.AddDays(-30):yyyy-MM-dd}");
        Assert.Equal(JsonValueKind.Null, before.GetProperty("cycleId").ValueKind);
        Assert.Equal([(0m, 40m, "NotSeeded")], Portions(before.GetProperty("lanes")[0]));
        Assert.Equal([(0m, 50m, "NotSeeded")], Portions(before.GetProperty("lanes")[1]));
        Assert.All(before.GetProperty("lanes").EnumerateArray(), l => Assert.Equal("NotSeeded", l.Text("summary")));
    }

    [Fact]
    public async Task A_closed_cycle_is_shown_by_its_id_or_by_a_date_within_it_and_a_foreign_cycle_is_refused()
    {
        var owner = await factory.OwnerAsync();
        var zoneId = (await owner.Client.CreateZoneAsync()).Id();
        var raft = await owner.Client.CreateRaftAsync(zoneId, NewRaftBody(lanes: 1));
        var other = await owner.Client.CreateRaftAsync(zoneId, NewRaftBody(lanes: 1));
        var closed = await factory.AddCycleAsync(raft.Id(), DateTimeOffset.UtcNow.AddDays(-90), DateTimeOffset.UtcNow.AddDays(-30));
        await factory.AddSegmentAsync(closed.Id, raft.LaneId(1), LaneSegmentType.Seeded, 0, 40, Today.AddDays(-85), owner.Account.Id);
        await factory.AddSegmentAsync(closed.Id, raft.LaneId(1), LaneSegmentType.Harvested, 0, 40, Today.AddDays(-35), owner.Account.Id);
        var open = await factory.AddCycleAsync(raft.Id(), DateTimeOffset.UtcNow.AddDays(-5));
        var foreign = await factory.AddCycleAsync(other.Id(), DateTimeOffset.UtcNow.AddDays(-5));

        var byId = await owner.Client.GetFromJsonAsync<JsonElement>($"/api/rafts/{raft.Id()}/condition?cycleId={closed.Id}");
        Assert.Equal(closed.Id, byId.GetProperty("cycleId").GetGuid());
        Assert.Equal("FullyHarvested", byId.GetProperty("lanes")[0].Text("summary"));

        var byDate = await owner.Client.GetFromJsonAsync<JsonElement>($"/api/rafts/{raft.Id()}/condition?date={Today.AddDays(-60):yyyy-MM-dd}");
        Assert.Equal(closed.Id, byDate.GetProperty("cycleId").GetGuid());
        Assert.Equal([(0m, 40m, "Growing")], Portions(byDate.GetProperty("lanes")[0]));

        var today = await owner.Client.GetFromJsonAsync<JsonElement>($"/api/rafts/{raft.Id()}/condition");
        Assert.Equal(open.Id, today.GetProperty("cycleId").GetGuid());
        Assert.Equal("NotSeeded", today.GetProperty("lanes")[0].Text("summary"));

        Assert.Equal(["Cycle"], await (await owner.Client.GetAsync($"/api/rafts/{raft.Id()}/condition?cycleId={foreign.Id}")).InvalidFieldsAsync());
        Assert.Equal(["Cycle"], await (await owner.Client.GetAsync($"/api/rafts/{raft.Id()}/condition?cycleId={Guid.NewGuid()}")).InvalidFieldsAsync());
    }

    [Fact]
    public async Task Staff_of_the_zone_read_the_condition_view_and_staff_of_another_zone_get_MSG02()
    {
        var owner = await factory.OwnerAsync();
        var zoneA = (await owner.Client.CreateZoneAsync()).Id();
        var zoneB = (await owner.Client.CreateZoneAsync()).Id();
        var raft = await owner.Client.CreateRaftAsync(zoneA);
        var inZone = await factory.StaffAsync(await factory.ZoneEntityAsync(zoneA));
        var elsewhere = await factory.StaffAsync(await factory.ZoneEntityAsync(zoneB));

        Assert.Equal(HttpStatusCode.OK, (await inZone.Client.GetAsync($"/api/rafts/{raft.Id()}/condition?date={Today:yyyy-MM-dd}")).StatusCode);
        var refused = await elsewhere.Client.GetAsync($"/api/rafts/{raft.Id()}/condition");
        Assert.Equal(HttpStatusCode.Forbidden, refused.StatusCode);
        Assert.Equal("MSG02", await refused.ErrorCodeAsync());
    }

    private static List<(decimal From, decimal To, string Condition)> Portions(JsonElement lane) =>
        lane.GetProperty("portions").EnumerateArray()
            .Select(p => (p.GetProperty("from").GetDecimal(), p.GetProperty("to").GetDecimal(), p.Text("condition")))
            .ToList();
}
