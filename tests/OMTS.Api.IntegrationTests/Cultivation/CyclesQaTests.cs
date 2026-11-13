using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using OMTS.Api.IntegrationTests.Farming;
using OMTS.Api.IntegrationTests.Infrastructure;
using OMTS.Domain.Constants;
using OMTS.Domain.Entities.Cultivation;
using OMTS.Domain.Entities.Farming;
using static OMTS.Api.IntegrationTests.Farming.FarmingTestKit;

namespace OMTS.Api.IntegrationTests.Cultivation;

/// <summary>Independent QA of CYCLE-01 and CYCLE-05. A test that names a finding holds its fix.</summary>
public class CyclesQaTests(AuthApiFactory factory) : IClassFixture<AuthApiFactory>
{
    private async Task<(HttpClient Owner, Guid ZoneId, JsonElement Raft)> OwnerWithRaftAsync(int lanes = 3)
    {
        var owner = (await factory.OwnerAsync()).Client;
        var zoneId = (await owner.CreateZoneAsync()).Id();
        return (owner, zoneId, await owner.CreateRaftAsync(zoneId, NewRaftBody(lanes: lanes)));
    }

    // GBR-02: the person, the zone and the values before and after of each change.
    [Fact]
    public async Task The_audit_log_keeps_the_person_and_the_values_of_a_start_an_edit_and_a_cancel()
    {
        var (_, zoneId, raft) = await OwnerWithRaftAsync();
        var manager = await factory.StaffAsync(await factory.ZoneEntityAsync(zoneId));
        var (start, moved) = (Today.AddDays(-9), Today.AddDays(-4));

        var cycle = await manager.Client.StartCycleAsync(raft.Id(), new CycleBody(start, 250.5m, null, 900m));
        var edited = await manager.Client.EditCycleAsync(cycle.Id(), new CycleBody(moved, 250.5m, Today.AddDays(9), null, cycle.Text("rowVersion")));
        await (await manager.Client.CancelCycleAsync(cycle.Id(), edited.Text("rowVersion"))).ReadAsync(HttpStatusCode.NoContent);

        var rows = await factory.QueryAsync(db => db.AuditEvents.Where(e => e.RecordId == cycle.Id()).ToListAsync());
        Assert.All(rows, r => Assert.Equal((manager.Account.Id, zoneId, raft.Text("code"), "CultivationCycle"), (r.ActorAccountId!.Value, r.ZoneId!.Value, r.RecordCode!, r.RecordType)));

        var started = rows.Single(r => r.Action == "CultivationCycle.Started");
        Assert.Null(started.Before);
        Assert.Contains($"{start:yyyy-MM-dd}", started.After!);
        Assert.Contains("250.5", started.After!);

        var updated = rows.Single(r => r.Action == "CultivationCycle.Updated");
        Assert.Contains($"{start:yyyy-MM-dd}", updated.Before!);
        Assert.Contains("900", updated.Before!);
        Assert.Contains($"{moved:yyyy-MM-dd}", updated.After!);
        Assert.DoesNotContain("250.5", updated.Before!);

        var cancelled = rows.Single(r => r.Action == "CultivationCycle.Cancelled");
        Assert.Contains($"{moved:yyyy-MM-dd}", cancelled.Before!);
        Assert.Null(cancelled.After);
    }

    // A cancelled cycle is gone: the view shows no cycle on its dates, and the raft starts again on the same date.
    [Fact]
    public async Task A_cancelled_cycle_leaves_no_trace_on_the_views_and_the_raft_starts_again_on_the_same_date()
    {
        var (owner, zoneId, raft) = await OwnerWithRaftAsync();
        var start = Today.AddDays(-3);
        var cycle = await owner.StartCycleAsync(raft.Id(), new CycleBody(start));
        await (await owner.CancelCycleAsync(cycle.Id(), cycle.Text("rowVersion"))).ReadAsync(HttpStatusCode.NoContent);

        var inside = await owner.ProgressAsync(raft.Id(), $"?date={start:yyyy-MM-dd}");
        Assert.True(inside.IsNull("cycle"));
        Assert.Equal(HttpStatusCode.NotFound, (await owner.GetAsync($"/api/rafts/{raft.Id()}/progress?cycleId={cycle.Id()}")).StatusCode);
        Assert.Equal(0, (await owner.GetFromJsonAsync<JsonElement>($"/api/rafts/{raft.Id()}/cultivation-cycles")).GetArrayLength());
        var row = await owner.ZoneRowAsync(zoneId, raft.Id());
        Assert.True(row.Flag("canStart") && row.IsNull("cycle") && row.IsNull("lastCloseDate"));

        Assert.Equal(start, (await owner.StartCycleAsync(raft.Id(), new CycleBody(start))).Date("startDate"));
    }

    [Fact]
    public async Task The_lanes_of_today_of_a_raft_with_no_cycle_are_not_seeded_removed_ones_included_also_when_discontinued()
    {
        var (owner, _, raft) = await OwnerWithRaftAsync();
        var fewer = await owner.EditRaftAsync(raft.Id(), NewRaftBody(raft.Text("code"), lanes: 2) with { RowVersion = raft.Text("rowVersion") });

        var today = await owner.GetFromJsonAsync<JsonElement>($"/api/rafts/{raft.Id()}/lanes/today");
        Assert.Equal((Today, 3), (today.Date("date"), today.GetProperty("lanes").GetArrayLength()));
        Assert.All(today.GetProperty("lanes").EnumerateArray(), l => Assert.Equal("NotSeeded", l.Text("state")));

        await (await owner.PostAsJsonAsync($"/api/rafts/{raft.Id()}/discontinue", new { rowVersion = fewer.Text("rowVersion") })).ReadAsync(HttpStatusCode.OK);
        Assert.Equal(3, (await owner.GetFromJsonAsync<JsonElement>($"/api/rafts/{raft.Id()}/lanes/today")).GetProperty("lanes").GetArrayLength());
        Assert.Equal(2, (await owner.ProgressAsync(raft.Id())).GetProperty("notSeededLanes").GetArrayLength());
    }

    // Rounded to the column first, and then checked: decimal(18,3) and decimal(18,2).
    [Theory]
    [InlineData("0.0005", "0.005", "0.001", "0.01")]
    [InlineData("1.0004", "1.004", "1", "1")]
    [InlineData("1.9995", "1.995", "2", "2")]
    [InlineData("999999999999999.9994", "9999999999999999.994", "999999999999999.999", "9999999999999999.99")]
    public async Task A_plan_estimate_is_rounded_to_its_column_and_answered_so_on_start_and_edit(string seed, string yield, string storedSeed, string storedYield)
    {
        var (owner, _, raft) = await OwnerWithRaftAsync();

        var cycle = await owner.StartCycleAsync(raft.Id(), new CycleBody(Today, decimal.Parse(seed), null, decimal.Parse(yield)));
        Assert.Equal((decimal.Parse(storedSeed), decimal.Parse(storedYield)), (cycle.GetProperty("expectedSeedQuantity").GetDecimal(), cycle.GetProperty("targetYieldKg").GetDecimal()));

        var edited = await owner.EditCycleAsync(cycle.Id(), new CycleBody(Today, decimal.Parse(seed), null, decimal.Parse(yield), cycle.Text("rowVersion")));
        Assert.Equal((decimal.Parse(storedSeed), decimal.Parse(storedYield)), (edited.GetProperty("expectedSeedQuantity").GetDecimal(), edited.GetProperty("targetYieldKg").GetDecimal()));
    }

    [Theory]
    [InlineData("0.0004", "1", "Expected seed quantity")]
    [InlineData("-0.0005", "1", "Expected seed quantity")]
    [InlineData("999999999999999.9995", "1", "Expected seed quantity")]
    [InlineData("1", "0.004", "Target yield")]
    [InlineData("1", "-0.005", "Target yield")]
    [InlineData("1", "9999999999999999.995", "Target yield")]
    public async Task A_plan_estimate_that_rounds_to_zero_or_out_of_the_column_is_refused(string seed, string yield, string field)
    {
        var (owner, _, raft) = await OwnerWithRaftAsync();

        Assert.Equal([field], await (await owner.PostCycleAsync(raft.Id(), new CycleBody(Today, decimal.Parse(seed), null, decimal.Parse(yield)))).InvalidFieldsAsync());
    }

    // Owner decision of 12/11/2026: the plan has no expected harvest date, so one sent (even before the start) is neither checked nor kept.
    [Fact]
    public async Task An_expected_harvest_date_sent_with_a_start_is_ignored()
    {
        var (owner, _, raft) = await OwnerWithRaftAsync();

        var cycle = await owner.StartCycleAsync(raft.Id(), new CycleBody(Today.AddDays(-2), null, Today.AddDays(-3)));

        Assert.False(cycle.TryGetProperty("expectedHarvestDate", out _));
    }

    // CYCLE-01.0 step 2: a start needs no value, but it needs a body. Note N1, as it stands: no body at all is a bare 415 with no MSG code, as on every JSON route.
    [Fact]
    public async Task A_start_with_an_empty_json_object_starts_today_and_one_with_no_body_answers_415_and_starts_nothing()
    {
        var (owner, _, raft) = await OwnerWithRaftAsync();

        var none = await owner.PostAsync($"/test-only/rafts/{raft.Id()}/cultivation-cycles", null);
        Assert.Equal(HttpStatusCode.UnsupportedMediaType, none.StatusCode);
        Assert.False(await factory.QueryAsync(db => db.CultivationCycles.AnyAsync(c => c.RaftId == raft.Id())));

        var empty = await owner.PostAsync($"/test-only/rafts/{raft.Id()}/cultivation-cycles", StepFormKit.FormOf(null, photos: 1));
        Assert.Equal(Today, (await empty.ReadAsync(HttpStatusCode.Created)).GetProperty("cycle").Date("startDate"));
    }

    [Fact]
    public async Task A_discontinued_zone_keeps_its_history_for_its_manager_and_refuses_a_start_naming_the_zone()
    {
        var (owner, zoneId, raft) = await OwnerWithRaftAsync();
        var closed = await factory.AddCycleAsync(raft.Id(), Today.AddDays(-9), Today.AddDays(-2));
        var manager = await factory.StaffAsync(await factory.ZoneEntityAsync(zoneId));
        await factory.UpdateAsync<FarmingZone>(zoneId, z => z.StatusId = StatusIds.Usage.Discontinued);

        var row = await manager.Client.ZoneRowAsync(zoneId, raft.Id());
        Assert.False(row.Flag("canStart"));
        Assert.Equal(closed.Id, row.GetProperty("cycle").Id());
        Assert.Equal(closed.Id, (await manager.Client.ProgressAsync(raft.Id(), $"?cycleId={closed.Id}")).GetProperty("cycle").Id());

        var refused = await (await manager.Client.PostCycleAsync(raft.Id())).ReadAsync(HttpStatusCode.Conflict);
        Assert.Equal("MSG-GEN-09", refused.Text("code"));
        Assert.Contains("Farming zone", refused.Text("detail"));
    }

    // CYCLE-05: a date with a closed cycle, a date between two cycles, the far past. The date is shown as asked.
    [Fact]
    public async Task The_progress_view_on_awkward_dates_answers_the_cycle_open_then()
    {
        var (owner, _, raft) = await OwnerWithRaftAsync();
        var closed = await factory.AddCycleAsync(raft.Id(), Today.AddDays(-20), Today.AddDays(-20));
        var open = await owner.StartCycleAsync(raft.Id(), new CycleBody(Today.AddDays(-10)));

        Assert.Equal(closed.Id, (await owner.ProgressAsync(raft.Id(), $"?date={Today.AddDays(-20):yyyy-MM-dd}")).GetProperty("cycle").Id());
        Assert.Equal(1, (await owner.ProgressAsync(raft.Id(), $"?cycleId={closed.Id}")).GetProperty("cycle").GetProperty("daysOfFarming").GetInt32());
        Assert.True((await owner.ProgressAsync(raft.Id(), $"?date={Today.AddDays(-15):yyyy-MM-dd}")).IsNull("cycle"));
        Assert.True((await owner.ProgressAsync(raft.Id(), "?date=0001-01-01")).IsNull("cycle"));
        Assert.Equal(open.Id(), (await owner.ProgressAsync(raft.Id(), $"?cycleId={open.Id()}")).GetProperty("cycle").Id());
        var future = await owner.GetAsync($"/api/rafts/{raft.Id()}/progress?date=9999-12-31");
        Assert.False(string.IsNullOrEmpty((await future.ReadAsync(HttpStatusCode.BadRequest)).Text("hint")));
        Assert.Equal("MSG-GEN-01", await future.ErrorCodeAsync());
    }

    // Finding L4. CYCLE-05.1 shows "the state of each lane on that date": lanes added after the close are not on the closed cycle, nor counted in notSeededLanes.
    // They are on the views of today, and on no earlier date without a cycle.
    [Fact]
    public async Task Lanes_added_after_a_cycle_closed_are_not_on_its_view()
    {
        var (owner, _, raft) = await OwnerWithRaftAsync(lanes: 3);
        var closed = await factory.AddCycleAsync(raft.Id(), Today.AddDays(-20), Today.AddDays(-10));
        await owner.EditRaftAsync(raft.Id(), NewRaftBody(raft.Text("code"), lanes: 5) with { RowVersion = raft.Text("rowVersion") });

        var view = await owner.ProgressAsync(raft.Id(), $"?cycleId={closed.Id}");

        Assert.Equal([1, 2, 3], view.GetProperty("notSeededLanes").EnumerateArray().Select(n => n.GetInt32()));
        Assert.Equal([1, 2, 3], view.LaneNumbers(StatusIds.Lane.InUse));
        Assert.Equal([1, 2, 3], (await owner.ProgressAsync(raft.Id(), $"?cycleId={closed.Id}&date={Today.AddDays(-20):yyyy-MM-dd}")).LaneNumbers(StatusIds.Lane.InUse));

        var today = await owner.ProgressAsync(raft.Id());
        Assert.Equal([1, 2, 3, 4, 5], today.LaneNumbers(StatusIds.Lane.InUse));
        Assert.Equal([1, 2, 3, 4, 5], today.GetProperty("notSeededLanes").EnumerateArray().Select(n => n.GetInt32()));
        Assert.Equal([1, 2, 3, 4, 5], (await owner.GetFromJsonAsync<JsonElement>($"/api/rafts/{raft.Id()}/lanes/today")).LaneNumbers(StatusIds.Lane.InUse));
        Assert.Empty((await owner.ProgressAsync(raft.Id(), $"?date={Today.AddDays(-5):yyyy-MM-dd}")).LaneNumbers(StatusIds.Lane.InUse));
    }

    // L4 must not empty a cycle whose start is dated back on a raft recorded later, as every demo cycle is: its lanes are on each of its days.
    [Fact]
    public async Task An_open_cycle_dated_back_keeps_its_lanes_on_every_day_of_it()
    {
        var (owner, _, raft) = await OwnerWithRaftAsync(lanes: 3);
        var start = Today.AddDays(-30);
        var open = await owner.StartCycleAsync(raft.Id(), new CycleBody(start));

        foreach (var query in new[] { "", $"?cycleId={open.Id()}", $"?date={start:yyyy-MM-dd}", $"?cycleId={open.Id()}&date={start.AddDays(10):yyyy-MM-dd}" })
        {
            var view = await owner.ProgressAsync(raft.Id(), query);
            Assert.Equal([1, 2, 3], view.LaneNumbers(StatusIds.Lane.InUse));
            Assert.Equal([1, 2, 3], view.GetProperty("notSeededLanes").EnumerateArray().Select(n => n.GetInt32()));
        }

        Assert.Equal(3, (await owner.GetFromJsonAsync<JsonElement>($"/api/rafts/{raft.Id()}/lanes/today")).GetProperty("lanes").GetArrayLength());
        Assert.Empty((await owner.ProgressAsync(raft.Id(), $"?date={start.AddDays(-1):yyyy-MM-dd}")).LaneNumbers(StatusIds.Lane.InUse));
    }

    // Finding L1. The list says "commands are true only when the use case would accept them" (CYCLE-01 PRE-3): canEdit and canCancel check the zone
    // as EditRefusal does, so a raft In Use in a Discontinued zone offers neither.
    [Fact]
    public async Task A_cycle_in_a_discontinued_zone_does_not_offer_edit_or_cancel()
    {
        var (owner, zoneId, raft) = await OwnerWithRaftAsync();
        var cycle = await owner.StartCycleAsync(raft.Id());
        await factory.UpdateAsync<FarmingZone>(zoneId, z => z.StatusId = StatusIds.Usage.Discontinued);

        Assert.Equal("MSG-GEN-09", await (await owner.PutCycleAsync(cycle.Id(), new CycleBody(Today, 3m, RowVersion: cycle.Text("rowVersion")))).ErrorCodeAsync());

        var row = await owner.ZoneRowAsync(zoneId, raft.Id());
        Assert.False(row.Flag("canEdit"), "zone list canEdit");
        Assert.False(row.Flag("canCancel"), "zone list canCancel");
        var view = await owner.GetFromJsonAsync<JsonElement>($"/api/cultivation-cycles/{cycle.Id()}");
        Assert.False(view.Flag("canEdit"), "cycle canEdit");
        Assert.False(view.Flag("canCancel"), "cycle canCancel");
        var onProgress = (await owner.ProgressAsync(raft.Id())).GetProperty("cycle");
        Assert.False(onProgress.Flag("canEdit"), "progress canEdit");
        Assert.False(onProgress.Flag("canCancel"), "progress canCancel");
    }

    // Finding L2. MSG-GEN-06 is "Blocked because related records are still in use"; MSG-GEN-09 is for "a discontinued, locked or closed record",
    // and the cycle is none of the three.
    [Fact]
    public async Task A_cancel_with_records_answers_msg_gen_06()
    {
        var (owner, _, raft) = await OwnerWithRaftAsync();
        var cycle = await owner.StartCycleAsync(raft.Id());
        await factory.UseDbAsync(db => db.SeedingRecords.Add(new SeedingRecord { CycleId = cycle.Id(), SeedLotLineId = Guid.NewGuid(), SeedingDate = Today, Quantity = 1, RecordedByAccountId = Guid.NewGuid() }));

        Assert.Equal("MSG-GEN-06", await (await owner.CancelCycleAsync(cycle.Id(), cycle.Text("rowVersion"))).ErrorCodeAsync());
    }

    // Finding L3. MSG-GEN-06 also reads "or a cycle is open", and the lane change and the discontinue of this very raft answer it too, with the same cycleId.
    [Fact]
    public async Task A_start_on_a_raft_with_an_open_cycle_answers_msg_gen_06_as_the_lane_change_does()
    {
        var (owner, _, raft) = await OwnerWithRaftAsync();
        await owner.StartCycleAsync(raft.Id());

        Assert.Equal("MSG-GEN-06", await (await owner.PostCycleAsync(raft.Id())).ErrorCodeAsync());
    }
}
