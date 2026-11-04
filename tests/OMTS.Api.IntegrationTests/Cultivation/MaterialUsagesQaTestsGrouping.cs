using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using OMTS.Api.IntegrationTests.Farming;
using OMTS.Api.IntegrationTests.Infrastructure;
using OMTS.Domain.Constants;
using OMTS.Domain.Entities.Farming;
using static OMTS.Api.IntegrationTests.Farming.FarmingTestKit;

namespace OMTS.Api.IntegrationTests.Cultivation;

/// <summary>QA of SCR-CYCLE-03: which group a record is in as its cycle changes, the order of groups, repairs and records, <c>locked</c>, <c>canEdit</c> and <c>canRecord</c>.</summary>
public class MaterialUsagesQaTestsGrouping(AuthApiFactory factory) : IClassFixture<AuthApiFactory>
{
    private static List<JsonElement> Groups(JsonElement list) => list.GetProperty("groups").EnumerateArray().ToList();

    private static JsonElement FirstRecord(JsonElement group) => group.GetProperty("repairs")[0].GetProperty("records")[0];

    [Fact]
    public async Task A_cancel_puts_the_records_in_the_open_group_with_no_lock_and_they_can_be_edited()
    {
        var setup = await factory.CycleAsync();
        var owner = setup.Owner.Client;
        var record = await owner.RecordUsageAsync(setup.RaftId, await factory.ItemAsync(), Today.AddDays(-5));
        var cycle = await owner.GetFromJsonAsync<JsonElement>($"/api/cultivation-cycles/{setup.CycleId}");

        Assert.Equal(HttpStatusCode.NoContent, (await owner.CancelCycleAsync(setup.CycleId, cycle.Text("rowVersion"))).StatusCode);

        var group = Groups(await owner.UsageListAsync(setup.RaftId)).Single();
        Assert.True(group.IsNull("cycle"));
        Assert.False(group.Flag("locked"));
        Assert.True(FirstRecord(group).IsNull("cycleId"));
        Assert.True(FirstRecord(group).Flag("canEdit"));
        var read = await owner.GetUsageAsync(record.Id());
        await (await owner.PutUsageAsync(record.Id(), read.AsEdit() with { Quantity = 6m })).ReadAsync(HttpStatusCode.OK);
    }

    // The new start date decides on its own day too: the record on it stays in, the one the day before goes out.
    [Fact]
    public async Task A_new_start_date_splits_the_records_at_its_own_day()
    {
        var setup = await factory.CycleAsync();
        var owner = setup.Owner.Client;
        var item = await factory.ItemAsync();
        var dayBefore = await owner.RecordUsageAsync(setup.RaftId, item, Today.AddDays(-6));
        var onDay = await owner.RecordUsageAsync(setup.RaftId, item, Today.AddDays(-5));
        var cycle = await owner.GetFromJsonAsync<JsonElement>($"/api/cultivation-cycles/{setup.CycleId}");

        await owner.EditCycleAsync(setup.CycleId, new CycleBody(Today.AddDays(-5), RowVersion: cycle.Text("rowVersion")));

        var groups = (await owner.UsageListAsync(setup.RaftId)).GroupsOf();
        Assert.Equal([(Guid?)setup.CycleId, null], groups.Select(g => g.Cycle));
        Assert.Equal([onDay.Id()], groups[0].Records);
        Assert.Equal([dayBefore.Id()], groups[1].Records);
    }

    // A cycle started back over a date with no cycle takes the record in, and its close locks it (BR-CYCLE-04: "dated within it").
    [Fact]
    public async Task A_record_on_a_free_date_joins_a_cycle_started_back_over_it_and_is_locked_by_its_close()
    {
        var setup = await factory.RaftAsync();
        var owner = setup.Owner.Client;
        var record = await owner.RecordUsageAsync(setup.RaftId, await factory.ItemAsync(), Today.AddDays(-5));
        Assert.True(record.IsNull("cycleId"));

        var cycle = await owner.StartCycleAsync(setup.RaftId, new CycleBody(Today.AddDays(-10)));
        Assert.Equal(cycle.Id(), (await owner.GetUsageAsync(record.Id())).GetProperty("cycleId").GetGuid());

        await factory.CloseCycleAsync(cycle.Id());

        var read = await owner.GetUsageAsync(record.Id());
        Assert.False(read.Flag("canEdit"));
        Assert.True(Groups(await owner.UsageListAsync(setup.RaftId)).Single().Flag("locked"));
        Assert.Equal("MSG-GEN-09", (await (await owner.PutUsageAsync(record.Id(), read.AsEdit())).ReadAsync(HttpStatusCode.Conflict)).Text("code"));
    }

    [Fact]
    public async Task Groups_repairs_and_records_come_in_the_order_of_the_client_list()
    {
        var setup = await factory.RaftAsync();
        var owner = setup.Owner.Client;
        var older = await factory.AddCycleAsync(setup.RaftId, Today.AddDays(-90), Today.AddDays(-80));
        var newer = await factory.AddCycleAsync(setup.RaftId, Today.AddDays(-50), Today.AddDays(-40));
        var open = (await owner.StartCycleAsync(setup.RaftId, new CycleBody(Today.AddDays(-10)))).Id();
        var (itemA, itemB) = (MaterialUsageTestKit.NewItem(), MaterialUsageTestKit.NewItem());
        (itemA.Code, itemB.Code) = ($"A{itemA.Code}", $"B{itemB.Code}");
        await factory.UseDbAsync(db => db.FarmingInputItems.AddRange(itemA, itemB));

        // Saved in the wrong order on purpose.
        await InsertAsync(older.RaftId, itemB.Id, Today.AddDays(-85), "x");
        await InsertAsync(newer.RaftId, itemA.Id, Today.AddDays(-45), "x");
        await owner.RecordUsageAsync(setup.RaftId, itemA.Id, Today.AddDays(-20), reason: "between");
        await owner.RecordUsageAsync(setup.RaftId, itemA.Id, Today.AddDays(-100), reason: "before");
        await owner.PostUsageAsync(setup.RaftId, new UsageBody(Today.AddDays(-2), "b reason", [new UsageLine(itemB.Id), new UsageLine(itemA.Id)]));
        await owner.RecordUsageAsync(setup.RaftId, itemA.Id, Today.AddDays(-2), reason: "a reason");
        await owner.RecordUsageAsync(setup.RaftId, itemA.Id, Today.AddDays(-1), reason: "z reason");

        var groups = Groups(await owner.UsageListAsync(setup.RaftId));

        Assert.Equal([(Guid?)open, newer.Id, older.Id, null], groups.Select(g => g.IsNull("cycle") ? (Guid?)null : g.GetProperty("cycle").Id()));
        var repairs = groups[0].GetProperty("repairs").EnumerateArray().ToList();
        Assert.Equal(
            [(Today.AddDays(-1), "z reason"), (Today.AddDays(-2), "a reason"), (Today.AddDays(-2), "b reason")],
            repairs.Select(r => (r.Date("usageDate"), r.Text("repairReason"))));
        Assert.Equal([itemA.Code, itemB.Code], repairs[2].GetProperty("records").EnumerateArray().Select(r => r.Text("itemCode")));
        Assert.Equal(
            [Today.AddDays(-20), Today.AddDays(-100)],
            groups[3].GetProperty("repairs").EnumerateArray().Select(r => r.Date("usageDate")));
        Assert.Equal([false, true, true, false], groups.Select(g => g.Flag("locked")));
        Assert.Equal([true, false, false, true], groups.Select(g => FirstRecord(g).Flag("canEdit")));
    }

    // The two "repairs" a person would call one: the identity is the exact text, after the outer spaces only. Pinned, see the report, observations.
    [Fact]
    public async Task A_repair_is_the_exact_date_and_trimmed_text()
    {
        var setup = await factory.RaftAsync();
        var owner = setup.Owner.Client;
        var item = await factory.ItemAsync();
        await owner.RecordUsageAsync(setup.RaftId, item, reason: "broken buoy");
        await owner.RecordUsageAsync(setup.RaftId, item, reason: "  broken buoy ");
        await owner.RecordUsageAsync(setup.RaftId, item, reason: "Broken buoy");
        await owner.RecordUsageAsync(setup.RaftId, item, reason: "broken  buoy");

        var repairs = Groups(await owner.UsageListAsync(setup.RaftId)).Single().GetProperty("repairs").EnumerateArray().ToList();

        Assert.Equal(3, repairs.Count);
        Assert.Equal(2, repairs.Single(r => r.Text("repairReason") == "broken buoy").GetProperty("records").GetArrayLength());
    }

    // canRecord: the raft and zone In Use and the caller may record, for each kind of caller that can read. A closed cycle of today does not hide it (the date is checked on save).
    [Fact]
    public async Task Can_record_follows_the_raft_and_the_zone_for_each_caller_and_not_a_closed_cycle()
    {
        var setup = await factory.RaftAsync();
        var owner = setup.Owner.Client;
        var manager = await factory.ManagerAsync(setup.ZoneId);
        var allZones = await factory.StaffAsync(null);
        var clients = new[] { owner, manager.Client, allZones.Client };

        await factory.AddCycleAsync(setup.RaftId, Today.AddDays(-3), Today);
        foreach (var client in clients)
        {
            Assert.True((await client.UsageListAsync(setup.RaftId)).Flag("canRecord"));
        }

        await factory.DiscontinueRaftAsync(setup.RaftId);
        foreach (var client in clients)
        {
            Assert.False((await client.UsageListAsync(setup.RaftId)).Flag("canRecord"));
        }

        await factory.UpdateAsync<Raft>(setup.RaftId, r => r.StatusId = StatusIds.Usage.InUse);
        await factory.UpdateAsync<FarmingZone>(setup.ZoneId, z => z.StatusId = StatusIds.Usage.Discontinued);
        foreach (var client in clients)
        {
            Assert.False((await client.UsageListAsync(setup.RaftId)).Flag("canRecord"));
        }
    }

    // A resume gives the commands back: GBR-01 "with the same code and history".
    [Fact]
    public async Task A_resumed_raft_gets_its_commands_back_on_the_same_records()
    {
        var setup = await factory.RaftAsync();
        var owner = setup.Owner.Client;
        var record = await owner.RecordUsageAsync(setup.RaftId, await factory.ItemAsync());

        await factory.DiscontinueRaftAsync(setup.RaftId);
        var off = Groups(await owner.UsageListAsync(setup.RaftId)).Single();
        Assert.False(off.Flag("locked"));
        Assert.False(FirstRecord(off).Flag("canEdit"));

        await factory.UpdateAsync<Raft>(setup.RaftId, r => r.StatusId = StatusIds.Usage.InUse);
        Assert.True((await owner.GetUsageAsync(record.Id())).Flag("canEdit"));
    }

    // A record inserted straight into a cycle that is closed, as a record entered before the close would be.
    private async Task InsertAsync(Guid raftId, Guid itemId, DateOnly date, string reason)
    {
        var recorder = await factory.QueryAsync(db => db.Accounts.Select(a => a.Id).FirstAsync());
        await factory.UseDbAsync(db => db.MaterialUsages.Add(new OMTS.Domain.Entities.Cultivation.MaterialUsage
        {
            RaftId = raftId, ItemId = itemId, UsageDate = date, RepairReason = reason, Quantity = 1m, RecordedByAccountId = recorder,
        }));
    }
}
