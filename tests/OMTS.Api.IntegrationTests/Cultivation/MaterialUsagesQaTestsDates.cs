using System.Net;
using System.Text.Json;
using OMTS.Api.IntegrationTests.Farming;
using OMTS.Api.IntegrationTests.Infrastructure;
using OMTS.Domain.Entities.Cultivation;
using OMTS.Domain.Entities.Farming;
using static OMTS.Api.IntegrationTests.Farming.FarmingTestKit;

namespace OMTS.Api.IntegrationTests.Cultivation;

/// <summary>QA of the dates of BR-CYCLE-03 and BR-CYCLE-04: today in UTC+7, boundary days, several cycles on one raft, and a date edited into or out of a closed cycle.</summary>
public class MaterialUsagesQaTestsDates(AuthApiFactory factory) : IClassFixture<AuthApiFactory>
{
    private static DateTimeOffset MidnightInVietnam(DateOnly day) => new(day.ToDateTime(TimeOnly.MinValue), TimeSpan.FromHours(7));

    private static async Task<JsonElement> ConflictAsync(HttpResponseMessage answer) => await answer.ReadAsync(HttpStatusCode.Conflict);

    /// <summary>A raft with a closed cycle from -60 to -50, a closed one from -40 to -30, and an Open one from -10.</summary>
    private async Task<(HttpClient Owner, Guid RaftId, Guid ZoneId, CultivationCycle Older, CultivationCycle Newer, Guid Open, Guid ItemId)> ThreeCyclesAsync()
    {
        var setup = await factory.RaftAsync();
        var owner = setup.Owner.Client;
        var older = await factory.AddCycleAsync(setup.RaftId, Today.AddDays(-60), Today.AddDays(-50));
        var newer = await factory.AddCycleAsync(setup.RaftId, Today.AddDays(-40), Today.AddDays(-30));
        var open = await owner.StartCycleAsync(setup.RaftId, new CycleBody(Today.AddDays(-10)));
        return (owner, setup.RaftId, setup.ZoneId, older, newer, open.Id(), await factory.ItemAsync());
    }

    [Fact]
    public async Task Today_is_the_date_in_vietnam_a_minute_either_side_of_midnight_on_record_and_on_edit()
    {
        using var api = new CyclesQaClockApiFactory();
        var owner = (await api.OwnerAsync()).Client;
        var raftId = (await owner.CreateRaftAsync((await owner.CreateZoneAsync()).Id())).Id();
        var itemId = await api.ItemAsync();
        var (oldDay, newDay) = (Today.AddDays(-1), Today);

        api.Clock.At = MidnightInVietnam(newDay).AddMinutes(-1);
        Assert.Equal(["Repair date"], await (await owner.PostUsageAsync(raftId, new UsageBody(newDay, "x", [new UsageLine(itemId)]))).InvalidFieldsAsync());
        var record = await owner.RecordUsageAsync(raftId, itemId, oldDay);
        Assert.Equal(["Repair date"], await (await owner.PutUsageAsync(record.Id(), record.AsEdit() with { UsageDate = newDay })).InvalidFieldsAsync());

        api.Clock.At = MidnightInVietnam(newDay).AddMinutes(1);
        Assert.Equal(oldDay, DateOnly.FromDateTime(api.Clock.GetUtcNow().UtcDateTime));
        await owner.RecordUsageAsync(raftId, itemId, newDay);
        await (await owner.PutUsageAsync(record.Id(), record.AsEdit() with { UsageDate = newDay })).ReadAsync(HttpStatusCode.OK);
        Assert.Equal(["Repair date"], await (await owner.PostUsageAsync(raftId, new UsageBody(newDay.AddDays(1), "x", [new UsageLine(itemId)]))).InvalidFieldsAsync());
    }

    // The client list: the close day still belongs to the closed cycle, and tomorrow begins at midnight in Vietnam.
    [Fact]
    public async Task A_cycle_closed_today_refuses_today_until_midnight_in_vietnam()
    {
        using var api = new CyclesQaClockApiFactory();
        var owner = (await api.OwnerAsync()).Client;
        var raftId = (await owner.CreateRaftAsync((await owner.CreateZoneAsync()).Id())).Id();
        var itemId = await api.ItemAsync();
        var (closeDay, nextDay) = (Today.AddDays(-1), Today);
        var closed = await api.AddCycleAsync(raftId, closeDay.AddDays(-5), closeDay);

        api.Clock.At = MidnightInVietnam(nextDay).AddMinutes(-1);
        var refused = await ConflictAsync(await owner.PostUsageAsync(raftId, new UsageBody(closeDay, "x", [new UsageLine(itemId)])));
        Assert.Equal(("MSG-GEN-09", closed.Id), (refused.Text("code"), refused.GetProperty("cycleId").GetGuid()));

        api.Clock.At = MidnightInVietnam(nextDay).AddMinutes(1);
        Assert.True((await owner.RecordUsageAsync(raftId, itemId, nextDay)).IsNull("cycleId"));
    }

    // BR-CYCLE-03: outside closed cycles is all the rule asks. A date before the start of the Open cycle, and between cycles, goes to the group with no cycle.
    [Fact]
    public async Task Each_boundary_day_of_several_cycles_is_refused_or_goes_to_the_right_group()
    {
        var (owner, raftId, _, older, newer, open, itemId) = await ThreeCyclesAsync();

        foreach (var (date, closed) in new[]
        {
            (Today.AddDays(-60), older.Id), (Today.AddDays(-50), older.Id), (Today.AddDays(-40), newer.Id), (Today.AddDays(-30), newer.Id),
        })
        {
            var refused = await ConflictAsync(await owner.PostUsageAsync(raftId, new UsageBody(date, "x", [new UsageLine(itemId)])));
            Assert.Equal(("MSG-GEN-09", closed), (refused.Text("code"), refused.GetProperty("cycleId").GetGuid()));
        }

        foreach (var (date, cycle) in new (DateOnly, Guid?)[]
        {
            (Today.AddDays(-61), null), (Today.AddDays(-49), null), (Today.AddDays(-41), null), (Today.AddDays(-29), null), (Today.AddDays(-11), null),
            (Today.AddDays(-10), open), (Today.AddDays(-1), open), (Today, open),
        })
        {
            var record = await owner.RecordUsageAsync(raftId, itemId, date);
            Assert.Equal(cycle, record.IsNull("cycleId") ? null : record.GetProperty("cycleId").GetGuid());
            Assert.True(record.Flag("canEdit"));
        }
    }

    [Fact]
    public async Task A_date_edited_within_free_days_moves_between_groups_and_into_a_closed_cycle_is_refused_on_both_its_boundary_days()
    {
        var (owner, raftId, _, older, _, open, itemId) = await ThreeCyclesAsync();
        var record = await owner.RecordUsageAsync(raftId, itemId, Today.AddDays(-5));
        Assert.Equal(open, record.GetProperty("cycleId").GetGuid());

        var noCycle = (await (await owner.PutUsageAsync(record.Id(), record.AsEdit() with { UsageDate = Today.AddDays(-11) })).ReadAsync(HttpStatusCode.OK)).GetProperty("record");
        Assert.True(noCycle.IsNull("cycleId"));

        foreach (var date in new[] { Today.AddDays(-60), Today.AddDays(-50) })
        {
            var refused = await ConflictAsync(await owner.PutUsageAsync(record.Id(), noCycle.AsEdit() with { UsageDate = date }));
            Assert.Equal(("MSG-GEN-09", older.Id), (refused.Text("code"), refused.GetProperty("cycleId").GetGuid()));
            Assert.Contains($"{Today.AddDays(-60):dd/MM/yyyy} to {Today.AddDays(-50):dd/MM/yyyy}", refused.Text("detail"));
        }

        Assert.Equal(Today.AddDays(-11), (await owner.GetUsageAsync(record.Id())).Date("usageDate"));
        var back = (await (await owner.PutUsageAsync(record.Id(), noCycle.AsEdit() with { UsageDate = Today.AddDays(-10) })).ReadAsync(HttpStatusCode.OK)).GetProperty("record");
        Assert.Equal(open, back.GetProperty("cycleId").GetGuid());
    }

    // BR-CYCLE-04: a locked record stays where it is by any route: its date out of the cycle, another raft, another item, for the Owner and the manager.
    [Fact]
    public async Task A_locked_record_can_leave_its_closed_cycle_by_no_edit()
    {
        var setup = await factory.CycleAsync();
        var owner = setup.Owner.Client;
        var item = await factory.ItemAsync();
        var manager = await factory.ManagerAsync(setup.ZoneId);
        var other = (await owner.CreateRaftAsync(setup.ZoneId)).Id();
        var record = await owner.RecordUsageAsync(setup.RaftId, item, Today.AddDays(-3));
        await factory.CloseCycleAsync(setup.CycleId);
        var free = await factory.ItemAsync();

        foreach (var client in new[] { owner, manager.Client })
        {
            foreach (var edit in new[]
            {
                record.AsEdit() with { UsageDate = Today.AddDays(-20) },
                record.AsEdit() with { RaftId = other },
                record.AsEdit() with { ItemId = free },
                record.AsEdit() with { Quantity = 9m },
                record.AsEdit() with { RepairReason = "other" },
                record.AsEdit(),
            })
            {
                var refused = await ConflictAsync(await client.PutUsageAsync(record.Id(), edit));
                Assert.Equal(("MSG-GEN-09", setup.CycleId), (refused.Text("code"), refused.GetProperty("cycleId").GetGuid()));
            }
        }

        var read = await owner.GetUsageAsync(record.Id());
        Assert.Equal((setup.RaftId, Today.AddDays(-3), 2m), (read.GetProperty("raftId").GetGuid(), read.Date("usageDate"), read.GetProperty("quantity").GetDecimal()));
    }

    // A move checks the date against the cycles of the new raft: a date closed on the old raft is free on the new one and the reverse. Group and lock follow.
    [Fact]
    public async Task A_move_is_judged_by_the_cycles_of_the_new_raft_only()
    {
        var setup = await factory.RaftAsync();
        var owner = setup.Owner.Client;
        var item = await factory.ItemAsync();
        await factory.AddCycleAsync(setup.RaftId, Today.AddDays(-30), Today.AddDays(-20));
        var other = (await owner.CreateRaftAsync(setup.ZoneId)).Id();
        var open = await owner.StartCycleAsync(other, new CycleBody(Today.AddDays(-40)));
        var record = await owner.RecordUsageAsync(other, item, Today.AddDays(-25));
        Assert.Equal(open.Id(), record.GetProperty("cycleId").GetGuid());

        // Into the raft where this date is closed: refused.
        var refused = await ConflictAsync(await owner.PutUsageAsync(record.Id(), record.AsEdit() with { RaftId = setup.RaftId }));
        Assert.Equal("MSG-GEN-09", refused.Text("code"));

        // With a date free on that raft: moved, and the record leaves the Open cycle.
        var moved = (await (await owner.PutUsageAsync(record.Id(), record.AsEdit() with { RaftId = setup.RaftId, UsageDate = Today.AddDays(-19) })).ReadAsync(HttpStatusCode.OK)).GetProperty("record");
        Assert.True(moved.IsNull("cycleId"));
        Assert.True(moved.Flag("canEdit"));
    }

    // GBR-01 at the far end of an edit: an In Use raft in a Discontinued zone answers as the zone; nothing moves.
    [Fact]
    public async Task A_move_to_a_raft_of_a_discontinued_zone_names_the_zone()
    {
        var setup = await factory.CycleAsync();
        var owner = setup.Owner.Client;
        var record = await owner.RecordUsageAsync(setup.RaftId, await factory.ItemAsync());
        var zoneB = await owner.CreateZoneAsync();
        var raftB = (await owner.CreateRaftAsync(zoneB.Id())).Id();
        await factory.UpdateAsync<FarmingZone>(zoneB.Id(), z => z.StatusId = OMTS.Domain.Constants.StatusIds.Usage.Discontinued);

        var refused = await ConflictAsync(await owner.PutUsageAsync(record.Id(), record.AsEdit() with { RaftId = raftB }));

        Assert.Equal("MSG-GEN-09", refused.Text("code"));
        Assert.StartsWith($"Farming zone {zoneB.Text("code")}", refused.Text("detail"));
        Assert.Equal(setup.RaftId, (await owner.GetUsageAsync(record.Id())).GetProperty("raftId").GetGuid());
    }

    // The SRS sets no lower limit. Pinned so it is seen: see the report, P6.
    [Fact]
    public async Task A_date_centuries_old_is_accepted_and_shown_in_the_group_with_no_cycle()
    {
        var setup = await factory.CycleAsync();
        var record = await setup.Owner.Client.RecordUsageAsync(setup.RaftId, await factory.ItemAsync(), new DateOnly(1, 1, 1));

        Assert.True(record.IsNull("cycleId"));
        Assert.Null((await setup.Owner.Client.UsageListAsync(setup.RaftId)).GroupsOf().Single().Cycle);
    }
}
