using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using OMTS.Api.IntegrationTests.Farming;
using OMTS.Api.IntegrationTests.Infrastructure;
using OMTS.Domain.Constants;
using static OMTS.Api.IntegrationTests.Farming.FarmingTestKit;

namespace OMTS.Api.IntegrationTests.Cultivation;

/// <summary>
/// CYCLE-05 on a date and FARM-04.1 lanes of today (BR-FARM-03). Seeding and harvest records come with C2 and H1a, so through the API
/// every lane is Not seeded here; the other states are unit tested in LaneStateRulesTests.
/// </summary>
public class CultivationProgressTests(AuthApiFactory factory) : IClassFixture<AuthApiFactory>
{
    private async Task<(HttpClient Owner, JsonElement Raft)> OwnerWithRaftAsync(int lanes = 3)
    {
        var owner = (await factory.OwnerAsync()).Client;
        return (owner, await owner.CreateRaftAsync((await owner.CreateZoneAsync()).Id(), NewRaftBody(lanes: lanes)));
    }

    private static List<string> States(JsonElement view) => view.GetProperty("lanes").EnumerateArray().Select(l => l.Text("state")).ToList();

    private static List<int> NotSeeded(JsonElement view) => view.GetProperty("notSeededLanes").EnumerateArray().Select(n => n.GetInt32()).ToList();

    [Fact]
    public async Task A_raft_with_no_cycle_shows_today_every_lane_not_seeded_and_no_cycle()
    {
        var (owner, raft) = await OwnerWithRaftAsync();

        var view = await owner.ProgressAsync(raft.Id());

        Assert.Equal((raft.Id(), raft.Text("code"), Today), (view.GetProperty("raftId").GetGuid(), view.Text("raftCode"), view.Date("date")));
        Assert.True(view.IsNull("cycle"));
        Assert.Equal(["NotSeeded", "NotSeeded", "NotSeeded"], States(view));
        Assert.Equal([1, 2, 3], NotSeeded(view));
    }

    [Fact]
    public async Task An_open_cycle_shows_with_its_plan_on_any_day_since_its_start_and_not_before_it()
    {
        var (owner, raft) = await OwnerWithRaftAsync();
        var start = Today.AddDays(-5);
        var cycle = await owner.StartCycleAsync(raft.Id(), new CycleBody(start, 300m));

        var today = await owner.ProgressAsync(raft.Id());
        Assert.Equal((cycle.Id(), 300m, 6), (today.GetProperty("cycle").Id(), today.GetProperty("cycle").GetProperty("expectedSeedQuantity").GetDecimal(), today.GetProperty("cycle").GetProperty("daysOfFarming").GetInt32()));

        Assert.Equal(cycle.Id(), (await owner.ProgressAsync(raft.Id(), $"?date={start:yyyy-MM-dd}")).GetProperty("cycle").Id());
        var before = await owner.ProgressAsync(raft.Id(), $"?date={start.AddDays(-1):yyyy-MM-dd}");
        Assert.True(before.IsNull("cycle"));
        Assert.Equal(start.AddDays(-1), before.Date("date"));
    }

    // CYCLE-05.1: the close day belongs to the closed cycle; on the next day no cycle was open.
    [Fact]
    public async Task A_closed_cycle_shows_up_to_its_close_day_and_not_after_it()
    {
        var (owner, raft) = await OwnerWithRaftAsync();
        var close = Today.AddDays(-3);
        var closed = await factory.AddCycleAsync(raft.Id(), Today.AddDays(-10), close);

        Assert.Equal(closed.Id, (await owner.ProgressAsync(raft.Id(), $"?date={close:yyyy-MM-dd}")).GetProperty("cycle").Id());
        Assert.True((await owner.ProgressAsync(raft.Id(), $"?date={close.AddDays(1):yyyy-MM-dd}")).IsNull("cycle"));
        Assert.True((await owner.ProgressAsync(raft.Id())).IsNull("cycle"));
    }

    [Fact]
    public async Task Choosing_a_closed_cycle_shows_its_close_date_and_a_date_inside_it_stays_on_that_date()
    {
        var (owner, raft) = await OwnerWithRaftAsync();
        var (start, close) = (Today.AddDays(-10), Today.AddDays(-3));
        var closed = await factory.AddCycleAsync(raft.Id(), start, close);
        await owner.StartCycleAsync(raft.Id());

        var atClose = await owner.ProgressAsync(raft.Id(), $"?cycleId={closed.Id}");
        Assert.Equal((close, closed.Id, 8), (atClose.Date("date"), atClose.GetProperty("cycle").Id(), atClose.GetProperty("cycle").GetProperty("daysOfFarming").GetInt32()));
        Assert.False(atClose.GetProperty("cycle").Flag("canEdit"));

        var inside = await owner.ProgressAsync(raft.Id(), $"?cycleId={closed.Id}&date={start:yyyy-MM-dd}");
        Assert.Equal((start, closed.Id), (inside.Date("date"), inside.GetProperty("cycle").Id()));
    }

    [Fact]
    public async Task A_date_outside_the_cycle_chosen_or_in_the_future_answers_msg_gen_01_naming_the_date()
    {
        var (owner, raft) = await OwnerWithRaftAsync();
        var closed = await factory.AddCycleAsync(raft.Id(), Today.AddDays(-10), Today.AddDays(-3));

        foreach (var query in new[]
        {
            $"?cycleId={closed.Id}&date={Today.AddDays(-11):yyyy-MM-dd}",
            $"?cycleId={closed.Id}&date={Today.AddDays(-2):yyyy-MM-dd}",
            $"?date={Today.AddDays(1):yyyy-MM-dd}",
        })
        {
            Assert.Equal(["Date"], await (await owner.GetAsync($"/api/rafts/{raft.Id()}/progress{query}")).InvalidFieldsAsync());
        }

        var open = await owner.StartCycleAsync(raft.Id());
        Assert.Equal(["Date"], await (await owner.GetAsync($"/api/rafts/{raft.Id()}/progress?cycleId={open.Id()}&date={Today.AddDays(1):yyyy-MM-dd}")).InvalidFieldsAsync());
    }

    [Fact]
    public async Task A_cycle_of_another_raft_or_an_unknown_one_answers_404()
    {
        var (owner, raft) = await OwnerWithRaftAsync();
        var (otherOwner, other) = await OwnerWithRaftAsync();
        var foreign = await otherOwner.StartCycleAsync(other.Id());

        Assert.Equal(HttpStatusCode.NotFound, (await owner.GetAsync($"/api/rafts/{raft.Id()}/progress?cycleId={foreign.Id()}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await owner.GetAsync($"/api/rafts/{raft.Id()}/progress?cycleId={Guid.NewGuid()}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await owner.GetAsync($"/api/rafts/{Guid.NewGuid()}/progress")).StatusCode);
    }

    [Fact]
    public async Task A_date_that_cannot_be_read_answers_msg_gen_01()
    {
        var (owner, raft) = await OwnerWithRaftAsync();

        var fields = await (await owner.GetAsync($"/api/rafts/{raft.Id()}/progress?date=yesterday")).InvalidFieldsAsync();

        Assert.Contains("date", Assert.Single(fields), StringComparison.OrdinalIgnoreCase);
    }

    // BR-CYCLE-02, Q23: a Removed lane with no record in the cycle is not on the view and is not counted as not seeded.
    [Fact]
    public async Task Removed_lanes_with_no_record_leave_the_progress_view_but_stay_on_the_lanes_of_today()
    {
        var (owner, raft) = await OwnerWithRaftAsync();
        await owner.EditRaftAsync(raft.Id(), NewRaftBody(raft.Text("code"), lanes: 2) with { RowVersion = raft.Text("rowVersion") });

        var view = await owner.ProgressAsync(raft.Id());
        Assert.Equal([1, 2], view.LaneNumbers(StatusIds.Lane.InUse));
        Assert.Empty(view.LaneNumbers(StatusIds.Lane.Removed));
        Assert.Equal([1, 2], NotSeeded(view));

        var today = await owner.GetFromJsonAsync<JsonElement>($"/api/rafts/{raft.Id()}/lanes/today");
        Assert.Equal([3], today.LaneNumbers(StatusIds.Lane.Removed));
        Assert.Equal(["NotSeeded", "NotSeeded", "NotSeeded"], States(today));
    }

    // FARM-04.1: the former fields stay and the state is new; the state is worked out and has no column.
    [Fact]
    public async Task The_lanes_of_today_add_the_state_and_keep_the_status()
    {
        var (owner, raft) = await OwnerWithRaftAsync(lanes: 2);
        await owner.StartCycleAsync(raft.Id());

        var today = await owner.GetFromJsonAsync<JsonElement>($"/api/rafts/{raft.Id()}/lanes/today");

        Assert.Equal((raft.Id(), raft.Text("code"), Today), (today.GetProperty("raftId").GetGuid(), today.Text("raftCode"), today.Date("date")));
        var lane = today.GetProperty("lanes")[0];
        Assert.Equal((raft.LaneId(1), 1, StatusIds.Lane.InUse, "In Use", "NotSeeded"), (lane.Id(), lane.GetProperty("laneNumber").GetInt32(), lane.GetProperty("statusId").GetInt32(), lane.Text("status"), lane.Text("state")));
        Assert.Equal(["id", "laneNumber", "state", "status", "statusId"], lane.EnumerateObject().Select(p => p.Name).Order(StringComparer.Ordinal));
    }

    [Fact]
    public async Task The_raft_answer_keeps_its_lanes_without_a_state()
    {
        var (owner, raft) = await OwnerWithRaftAsync();

        var read = await owner.GetFromJsonAsync<JsonElement>($"/api/rafts/{raft.Id()}");

        Assert.False(read.GetProperty("lanes")[0].TryGetProperty("state", out _));
    }

    // PM 03/11: both the first and the last day count, on an Open cycle up to today.
    [Fact]
    public async Task The_cycle_answer_counts_the_days_of_farming_inclusively()
    {
        var (owner, raft) = await OwnerWithRaftAsync();
        var open = await owner.StartCycleAsync(raft.Id(), new CycleBody(Today.AddDays(-9)));

        var read = await owner.GetFromJsonAsync<JsonElement>($"/api/cultivation-cycles/{open.Id()}");

        Assert.Equal(10, read.GetProperty("daysOfFarming").GetInt32());
    }
}
