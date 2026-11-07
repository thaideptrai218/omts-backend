using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using OMTS.Api.IntegrationTests.Farming;
using OMTS.Api.IntegrationTests.Infrastructure;
using OMTS.Domain.Constants;
using OMTS.Domain.Entities.Cultivation;
using static OMTS.Api.IntegrationTests.Farming.FarmingTestKit;

namespace OMTS.Api.IntegrationTests.Cultivation;

/// <summary>SCR-CYCLE-03: by cycle and by repair, the dates with no cycle apart (BR-CYCLE-03). The cycle is never stored, so it follows the cycle dates.</summary>
public class MaterialUsageGroupingTests(AuthApiFactory factory) : IClassFixture<AuthApiFactory>
{
    // A record is entered before the close, as a closed date is refused.
    private async Task<JsonElement> RecordInsideAsync(HttpClient owner, CultivationCycle cycle, Guid itemId, DateOnly date)
    {
        var close = cycle.CloseDate!.Value;
        await factory.UpdateAsync<CultivationCycle>(cycle.Id, c => (c.CloseDate, c.StatusId) = (null, StatusIds.CultivationCycle.Open));
        var record = await owner.RecordUsageAsync(cycle.RaftId, itemId, date);
        await factory.UpdateAsync<CultivationCycle>(cycle.Id, c => (c.CloseDate, c.StatusId) = (close, StatusIds.CultivationCycle.Closed));
        return record;
    }

    [Fact]
    public async Task The_list_has_the_latest_cycle_first_the_dates_with_no_cycle_last_and_no_group_for_a_cycle_without_usage()
    {
        var setup = await factory.RaftAsync();
        var owner = setup.Owner.Client;
        var item = await factory.ItemAsync();
        var unused = await factory.AddCycleAsync(setup.RaftId, Today.AddDays(-90), Today.AddDays(-80));
        var closed = await factory.AddCycleAsync(setup.RaftId, Today.AddDays(-50), Today.AddDays(-40));
        var open = (await owner.StartCycleAsync(setup.RaftId, new CycleBody(Today.AddDays(-10)))).Id();

        var inClosed = await RecordInsideAsync(owner, closed, item, Today.AddDays(-45));
        var inOpen = await owner.RecordUsageAsync(setup.RaftId, item, Today.AddDays(-5));
        var between = await owner.RecordUsageAsync(setup.RaftId, item, Today.AddDays(-20));
        var before = await owner.RecordUsageAsync(setup.RaftId, item, Today.AddDays(-100));

        var list = await owner.UsageListAsync(setup.RaftId);

        Assert.Equal(setup.RaftId, list.GetProperty("raftId").GetGuid());
        Assert.True(list.Flag("canRecord"));
        var groups = list.GroupsOf();
        Assert.Equal([(Guid?)open, closed.Id, null], groups.Select(g => g.Cycle));
        Assert.DoesNotContain(unused.Id, groups.Select(g => g.Cycle ?? Guid.Empty));
        Assert.Equal([inOpen.Id()], groups[0].Records);
        Assert.Equal([inClosed.Id()], groups[1].Records);
        Assert.Equal([between.Id(), before.Id()], groups[2].Records);

        var byGroup = list.GetProperty("groups").EnumerateArray().ToList();
        Assert.False(byGroup[0].Flag("locked"));
        Assert.True(byGroup[1].Flag("locked"));
        Assert.False(byGroup[2].Flag("locked"));
        Assert.Equal(Today.AddDays(-50), byGroup[1].GetProperty("cycle").Date("startDate"));
        Assert.Equal(Today.AddDays(-40), byGroup[1].GetProperty("cycle").Date("closeDate"));
        Assert.Equal("Closed", byGroup[1].GetProperty("cycle").Text("status"));

        // The lock shows on the command of each record, and the cycle on each record.
        Assert.True(byGroup[0].GetProperty("repairs")[0].GetProperty("records")[0].Flag("canEdit"));
        Assert.False(byGroup[1].GetProperty("repairs")[0].GetProperty("records")[0].Flag("canEdit"));
        Assert.True(byGroup[2].GetProperty("repairs")[0].GetProperty("records")[0].Flag("canEdit"));
        Assert.Equal(closed.Id, byGroup[1].GetProperty("repairs")[0].GetProperty("records")[0].GetProperty("cycleId").GetGuid());
        Assert.True(byGroup[2].GetProperty("repairs")[0].GetProperty("records")[0].IsNull("cycleId"));
    }

    [Fact]
    public async Task Records_with_the_same_date_and_reason_form_one_repair_the_latest_first()
    {
        var setup = await factory.RaftAsync();
        var owner = setup.Owner.Client;
        var (buoy, rope) = (await factory.ItemAsync(), await factory.ItemAsync());
        var yesterday = Today.AddDays(-1);
        await owner.PostUsageAsync(setup.RaftId, new UsageBody(yesterday, "broken buoy", [new UsageLine(buoy), new UsageLine(rope)]));
        await owner.RecordUsageAsync(setup.RaftId, rope, yesterday, reason: "broken buoy");
        await owner.RecordUsageAsync(setup.RaftId, buoy, yesterday, reason: "snapped rope");
        await owner.RecordUsageAsync(setup.RaftId, buoy, Today, reason: "broken buoy");

        var repairs = (await owner.UsageListAsync(setup.RaftId)).GetProperty("groups")[0].GetProperty("repairs").EnumerateArray().ToList();

        Assert.Equal(
            [(Today, "broken buoy", 1), (yesterday, "broken buoy", 3), (yesterday, "snapped rope", 1)],
            repairs.Select(r => (r.Date("usageDate"), r.Text("repairReason"), r.GetProperty("records").GetArrayLength())));
    }

    [Fact]
    public async Task A_raft_with_no_usage_has_an_empty_list()
    {
        var setup = await factory.CycleAsync();
        var list = await setup.Owner.Client.UsageListAsync(setup.RaftId);
        Assert.Equal(0, list.GetProperty("groups").GetArrayLength());
    }

    // BR-CYCLE-01 counts only seeding sessions and logs, and the cycle is not stored: a cancel or a new start date moves the records to the group of their dates.
    [Fact]
    public async Task A_record_follows_the_cycle_dates_and_does_not_block_a_cancel_or_a_new_start_date()
    {
        var setup = await factory.CycleAsync();
        var owner = setup.Owner.Client;
        var record = await owner.RecordUsageAsync(setup.RaftId, await factory.ItemAsync(), Today.AddDays(-5));
        Assert.Equal(setup.CycleId, (await owner.UsageListAsync(setup.RaftId)).GroupsOf().Single().Cycle);

        var cycle = await owner.GetFromJsonAsync<JsonElement>($"/api/cultivation-cycles/{setup.CycleId}");
        Assert.True(cycle.Flag("canCancel"));
        var moved = await owner.EditCycleAsync(setup.CycleId, new CycleBody(Today.AddDays(-2), RowVersion: cycle.Text("rowVersion")));
        Assert.Null((await owner.UsageListAsync(setup.RaftId)).GroupsOf().Single().Cycle);
        Assert.True((await owner.GetUsageAsync(record.Id())).IsNull("cycleId"));

        var earlier = await owner.EditCycleAsync(setup.CycleId, new CycleBody(Today.AddDays(-6), RowVersion: moved.Text("rowVersion")));
        Assert.Equal(setup.CycleId, (await owner.UsageListAsync(setup.RaftId)).GroupsOf().Single().Cycle);

        Assert.Equal(HttpStatusCode.NoContent, (await owner.CancelCycleAsync(setup.CycleId, earlier.Text("rowVersion"))).StatusCode);
        Assert.Null((await owner.UsageListAsync(setup.RaftId)).GroupsOf().Single().Cycle);
        Assert.Equal(1, await factory.QueryAsync(db => db.MaterialUsages.CountAsync(u => u.RaftId == setup.RaftId)));
    }
}
