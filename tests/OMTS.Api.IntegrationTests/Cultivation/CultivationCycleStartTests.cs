using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using OMTS.Api.IntegrationTests.Farming;
using OMTS.Api.IntegrationTests.Infrastructure;
using OMTS.Domain.Constants;
using static OMTS.Api.IntegrationTests.Farming.FarmingTestKit;

namespace OMTS.Api.IntegrationTests.Cultivation;

/// <summary>CYCLE-01.0 Start cultivation cycle: BR-CYCLE-01 dates and plan, GBR-01, one Open cycle.</summary>
public class CultivationCycleStartTests(AuthApiFactory factory) : IClassFixture<AuthApiFactory>
{
    private async Task<(HttpClient Owner, JsonElement Raft)> OwnerWithRaftAsync(int lanes = 3)
    {
        var owner = (await factory.OwnerAsync()).Client;
        return (owner, await owner.CreateRaftAsync((await owner.CreateZoneAsync()).Id(), NewRaftBody(lanes: lanes)));
    }

    // Steps 2 and 5: today by default, Open, MSG-GEN-05, the lanes to check, and one audit row.
    [Fact]
    public async Task A_start_without_a_date_starts_today_and_answers_the_cycle_the_lanes_and_the_message()
    {
        var (owner, raft) = await OwnerWithRaftAsync(lanes: 2);

        var answer = await (await owner.PostCycleAsync(raft.Id())).ReadAsync(HttpStatusCode.Created);

        Assert.Equal(("Cultivation cycle saved.", "MSG-GEN-05"), (answer.Text("message"), answer.Text("messageCode")));
        Assert.True(answer.IsNull("previousCloseDate"));
        Assert.Equal([1, 2], answer.LaneNumbers(StatusIds.Lane.InUse));
        var cycle = answer.GetProperty("cycle");
        Assert.Equal((Today, StatusIds.CultivationCycle.Open, "Open"), (cycle.Date("startDate"), cycle.GetProperty("statusId").GetInt32(), cycle.Text("status")));
        Assert.Equal((raft.Id(), raft.Text("code")), (cycle.GetProperty("raftId").GetGuid(), cycle.Text("raftCode")));
        Assert.True(cycle.IsNull("closeDate") && cycle.IsNull("expectedSeedQuantity") && cycle.IsNull("targetYieldKg"));
        // The plan has no expected harvest date any more (owner decision of 12/11/2026) and the nursery fields are null on a cycle started the old way.
        Assert.False(cycle.TryGetProperty("expectedHarvestDate", out _));
        Assert.True(cycle.IsNull("nurseryBatchId") && cycle.IsNull("nurseryCode") && cycle.IsNull("nurseryAllocated"));
        Assert.True(cycle.Flag("canEdit") && cycle.Flag("canCancel"));
        Assert.Equal(1, cycle.GetProperty("daysOfFarming").GetInt32());

        var audit = await factory.QueryAsync(db => db.AuditEvents.SingleAsync(e => e.RecordId == cycle.Id()));
        Assert.Equal(("CultivationCycle.Started", "CultivationCycle", raft.Text("code")), (audit.Action, audit.RecordType, audit.RecordCode));
        Assert.Contains("startDate", audit.After!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task A_start_in_the_past_keeps_the_date_and_the_plan_and_counts_both_first_and_last_day()
    {
        var (owner, raft) = await OwnerWithRaftAsync();
        var start = Today.AddDays(-9);

        var cycle = await owner.StartCycleAsync(raft.Id(), new CycleBody(start, 250.5m, start.AddDays(1), 8000.25m));

        Assert.Equal(start, cycle.Date("startDate"));
        // The body still carries an expected harvest date: it is ignored, never stored or answered.
        Assert.Equal((250.5m, 8000.25m), (cycle.GetProperty("expectedSeedQuantity").GetDecimal(), cycle.GetProperty("targetYieldKg").GetDecimal()));
        Assert.False(cycle.TryGetProperty("expectedHarvestDate", out _));
        Assert.Null(await factory.QueryAsync(db => db.CultivationCycles.Where(c => c.Id == cycle.Id()).Select(c => c.ExpectedHarvestDate).SingleAsync()));
        Assert.Equal(10, cycle.GetProperty("daysOfFarming").GetInt32());
    }

    [Fact]
    public async Task A_start_date_after_today_is_refused()
    {
        var (owner, raft) = await OwnerWithRaftAsync();

        Assert.Equal(["Start date"], await (await owner.PostCycleAsync(raft.Id(), new CycleBody(Today.AddDays(1)))).InvalidFieldsAsync());

        Assert.False(await factory.QueryAsync(db => db.CultivationCycles.AnyAsync(c => c.RaftId == raft.Id())));
    }

    // BR-CYCLE-01: after the close date of the previous cycle, so a raft closed today can start from tomorrow (which is not today yet).
    [Theory]
    [InlineData(-3, -3, true)] // on the close date
    [InlineData(-5, -3, true)] // before the close date
    [InlineData(-2, -3, false)] // the day after it
    [InlineData(0, -1, false)] // closed yesterday, starts today
    [InlineData(0, 0, true)] // closed today, starts today
    public async Task The_start_date_must_be_after_the_close_date_of_the_previous_cycle(int startDays, int closeDays, bool refused)
    {
        var (owner, raft) = await OwnerWithRaftAsync();
        var closeDate = Today.AddDays(closeDays);
        await factory.AddCycleAsync(raft.Id(), Today.AddDays(-20), closeDate);

        var answer = await owner.PostCycleAsync(raft.Id(), new CycleBody(Today.AddDays(startDays)));

        if (refused)
        {
            Assert.Equal(["Start date"], await answer.InvalidFieldsAsync());
            return;
        }

        var saved = await answer.ReadAsync(HttpStatusCode.Created);
        Assert.Equal(closeDate, saved.Date("previousCloseDate"));
    }

    // Plan estimates: every invalid one is listed at once. A value that rounds to 0 at the scale of its column is 0, and one too large for it is no 500.
    [Fact]
    public async Task Invalid_plan_estimates_are_all_listed_and_nothing_is_saved()
    {
        var (owner, raft) = await OwnerWithRaftAsync();

        Assert.Equal(
            ["Expected seed quantity", "Target yield"],
            await (await owner.PostCycleAsync(raft.Id(), new CycleBody(Today, 0m, Today, -1m))).InvalidFieldsAsync());
        Assert.Equal(
            ["Expected seed quantity", "Target yield"],
            await (await owner.PostCycleAsync(raft.Id(), new CycleBody(Today, 0.0004m, null, 0.004m))).InvalidFieldsAsync());
        Assert.Equal(
            ["Expected seed quantity", "Target yield"],
            await (await owner.PostCycleAsync(raft.Id(), new CycleBody(Today, 1_000_000_000_000_000m, null, 10_000_000_000_000_000m))).InvalidFieldsAsync());

        Assert.False(await factory.QueryAsync(db => db.CultivationCycles.AnyAsync(c => c.RaftId == raft.Id())));
    }

    [Fact]
    public async Task Plan_estimates_are_answered_as_the_columns_store_them()
    {
        var (owner, raft) = await OwnerWithRaftAsync();

        var cycle = await owner.StartCycleAsync(raft.Id(), new CycleBody(Today, 1.23456m, null, 7.895m));

        Assert.Equal((1.235m, 7.90m), (cycle.GetProperty("expectedSeedQuantity").GetDecimal(), cycle.GetProperty("targetYieldKg").GetDecimal()));
    }

    // Named "request": the known behaviour of every unreadable JSON value of the API (see WaterBusinessTimeQaTests).
    [Theory]
    [InlineData("{\"startDate\":\"not a date\"}")]
    [InlineData("{\"targetYieldKg\":\"many\"}")]
    public async Task A_value_that_cannot_be_read_answers_msg_gen_01(string json)
    {
        var (owner, raft) = await OwnerWithRaftAsync();

        var answer = await owner.PostAsync($"/api/rafts/{raft.Id()}/cultivation-cycles", new StringContent(json, System.Text.Encoding.UTF8, "application/json"));

        Assert.Equal(["request"], await answer.InvalidFieldsAsync());
        Assert.False(await factory.QueryAsync(db => db.CultivationCycles.AnyAsync(c => c.RaftId == raft.Id())));
    }

    // GBR-01, CYCLE-01 PRE-3.
    [Fact]
    public async Task A_discontinued_raft_answers_msg_gen_09_and_starts_nothing()
    {
        var (owner, raft) = await OwnerWithRaftAsync();
        await (await owner.PostAsJsonAsync($"/api/rafts/{raft.Id()}/discontinue", new { rowVersion = raft.Text("rowVersion") })).ReadAsync(HttpStatusCode.OK);

        var refused = await (await owner.PostCycleAsync(raft.Id(), new CycleBody(Today.AddDays(1)))).SrsProblemAsync(HttpStatusCode.Conflict, "MSG-GEN-09", "MSG61");

        Assert.Equal($"Raft {raft.Text("code")} is Discontinued and cannot be used for this action.", refused.Detail());
        Assert.False(await factory.QueryAsync(db => db.CultivationCycles.AnyAsync(c => c.RaftId == raft.Id())));
    }

    // BR-CYCLE-01: at most one Open cycle per raft; the refusal names it.
    [Fact]
    public async Task A_second_start_answers_msg_gen_06_naming_the_open_cycle()
    {
        var (owner, raft) = await OwnerWithRaftAsync();
        var open = await owner.StartCycleAsync(raft.Id());

        var refused = await (await owner.PostCycleAsync(raft.Id())).SrsProblemAsync(HttpStatusCode.Conflict, "MSG-GEN-06", "MSG11");

        Assert.Equal($"Cannot proceed because 1 records are still in use: cultivation cycle started {Today:dd/MM/yyyy}. Please handle these records first.", refused.Detail());
        Assert.Equal(open.Id(), refused.GetProperty("cycleId").GetGuid());
        Assert.Single(await factory.QueryAsync(db => db.CultivationCycles.Where(c => c.RaftId == raft.Id()).ToListAsync()));
    }

    // FARM-04.2.E1 with a cycle started through the API, and the message now shows its start date.
    [Fact]
    public async Task A_started_cycle_blocks_a_lane_change_naming_its_start_date()
    {
        var (owner, raft) = await OwnerWithRaftAsync();
        var start = Today.AddDays(-2);
        var cycle = await owner.StartCycleAsync(raft.Id(), new CycleBody(start));
        var current = await owner.GetFromJsonAsync<JsonElement>($"/api/rafts/{raft.Id()}");

        var blocked = await (await owner.PutAsJsonAsync($"/api/rafts/{raft.Id()}", NewRaftBody(raft.Text("code"), lanes: 5) with { RowVersion = current.Text("rowVersion") }))
            .SrsProblemAsync(HttpStatusCode.Conflict, "MSG-GEN-06", "MSG11");

        Assert.Contains($"cultivation cycle started {start:dd/MM/yyyy}", blocked.Detail());
        Assert.Equal(cycle.Id(), blocked.GetProperty("cycleId").GetGuid());
    }

    // CYCLE-01 notifies nobody: seeding complete (C2) is the first notice of a cycle, and the PM decision of 03/11 spares an Owner their own action.
    [Fact]
    public async Task Starting_editing_and_cancelling_send_no_notification()
    {
        var (_, raft) = await OwnerWithRaftAsync();
        var manager = (await factory.StaffAsync(await factory.ZoneEntityAsync(raft.GetProperty("zone").Id()))).Client;
        var before = await factory.QueryAsync(db => db.Notifications.CountAsync());

        var cycle = await manager.StartCycleAsync(raft.Id());
        var edited = await manager.EditCycleAsync(cycle.Id(), new CycleBody(Today, 3m, RowVersion: cycle.Text("rowVersion")));
        await (await manager.CancelCycleAsync(cycle.Id(), edited.Text("rowVersion"))).ReadAsync(HttpStatusCode.NoContent);

        Assert.Equal(before, await factory.QueryAsync(db => db.Notifications.CountAsync()));
    }

    [Fact]
    public async Task An_unknown_raft_answers_404()
    {
        var owner = (await factory.OwnerAsync()).Client;

        Assert.Equal(HttpStatusCode.NotFound, (await owner.PostCycleAsync(Guid.NewGuid())).StatusCode);
    }
}
