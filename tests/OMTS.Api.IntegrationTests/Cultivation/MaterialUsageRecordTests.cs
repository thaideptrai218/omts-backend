using System.Net;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using OMTS.Api.IntegrationTests.Farming;
using OMTS.Api.IntegrationTests.Infrastructure;
using OMTS.Domain.Constants;
using OMTS.Domain.Entities.Farming;
using OMTS.Domain.Enums;
using static OMTS.Api.IntegrationTests.Farming.FarmingTestKit;

namespace OMTS.Api.IntegrationTests.Cultivation;

/// <summary>CYCLE-03.0, BR-CYCLE-03: one record per item, the fields, the group Material, the dates and the raft In Use.</summary>
public class MaterialUsageRecordTests(AuthApiFactory factory) : IClassFixture<AuthApiFactory>
{
    private static UsageBody Repair(params UsageLine[] lines) => new(Today, MaterialUsageTestKit.BrokenBuoy, lines);

    [Fact]
    public async Task A_repair_makes_one_record_per_item_with_one_audit_row_each_and_notifies_nobody()
    {
        var setup = await factory.CycleAsync();
        var manager = await factory.ManagerAsync(setup.ZoneId);
        var (buoy, rope) = (await factory.ItemAsync(), await factory.ItemAsync());
        var notifications = await factory.QueryAsync(db => db.Notifications.CountAsync());

        var saved = await (await manager.Client.PostUsageAsync(setup.RaftId, new UsageBody(Today.AddDays(-1), "  snapped rope ", [new UsageLine(buoy, 4m), new UsageLine(rope, 1.125m)])))
            .ReadAsync(HttpStatusCode.Created);

        Assert.Equal(("MSG-GEN-05", "Material usage saved."), (saved.Text("messageCode"), saved.Text("message")));
        var records = saved.GetProperty("records").EnumerateArray().ToList();
        Assert.Equal(2, records.Count);
        var first = records.Single(r => r.GetProperty("itemId").GetGuid() == buoy);
        Assert.Equal(4m, first.GetProperty("quantity").GetDecimal());
        Assert.Equal(1.125m, records.Single(r => r.GetProperty("itemId").GetGuid() == rope).GetProperty("quantity").GetDecimal());
        Assert.Equal("snapped rope", first.Text("repairReason"));
        Assert.Equal(Today.AddDays(-1), first.Date("usageDate"));
        Assert.Equal(setup.CycleId, first.GetProperty("cycleId").GetGuid());
        Assert.Equal(manager.Account.Id, first.GetProperty("recordedByAccountId").GetGuid());
        Assert.False(string.IsNullOrEmpty(first.Text("recordedByName")));
        Assert.Equal("piece", first.Text("unit"));
        Assert.True(first.Flag("canEdit"));

        // BR-FARM-05: no price and no cost on a record.
        Assert.DoesNotContain(first.EnumerateObject(), p => p.Name.Contains("price", StringComparison.OrdinalIgnoreCase) || p.Name.Contains("cost", StringComparison.OrdinalIgnoreCase));

        var audits = await factory.QueryAsync(db => db.AuditEvents.Where(e => e.Action == "MaterialUsage.Created" && e.ZoneId == setup.ZoneId).ToListAsync());
        Assert.Equal(records.Select(r => r.Id()).Order(), audits.Select(a => a.RecordId!.Value).Order());
        Assert.All(audits, a => Assert.Equal("MaterialUsage", a.RecordType));
        Assert.All(audits, a => Assert.Contains("snapped rope", a.After));
        Assert.Equal(notifications, await factory.QueryAsync(db => db.Notifications.CountAsync()));
    }

    [Fact]
    public async Task Every_invalid_field_is_named_with_msg_gen_01()
    {
        var setup = await factory.CycleAsync();
        var owner = setup.Owner.Client;
        var item = await factory.ItemAsync();

        Assert.Equal(
            ["Repair date", "Repair reason", "Items"],
            await (await owner.PostUsageAsync(setup.RaftId, new UsageBody(null, "  ", null))).InvalidFieldsAsync());
        Assert.Equal(["Repair date"], await (await owner.PostUsageAsync(setup.RaftId, new UsageBody(Today.AddDays(1), "x", [new UsageLine(item)]))).InvalidFieldsAsync());
        Assert.Equal(["Repair reason"], await (await owner.PostUsageAsync(setup.RaftId, new UsageBody(Today, new string('x', 501), [new UsageLine(item)]))).InvalidFieldsAsync());
        Assert.Equal(["Items"], await (await owner.PostUsageAsync(setup.RaftId, new UsageBody(Today, "x", []))).InvalidFieldsAsync());
        Assert.Equal(["Line 1 item"], await (await owner.PostUsageAsync(setup.RaftId, Repair(new UsageLine(null)))).InvalidFieldsAsync());
        Assert.Equal(["Line 1 item"], await (await owner.PostUsageAsync(setup.RaftId, Repair(new UsageLine(Guid.NewGuid())))).InvalidFieldsAsync());

        foreach (var quantity in new decimal?[] { null, 0m, -1m, 1.0005m, 1_000_000_001m })
        {
            Assert.Equal(["Line 2 quantity"], await (await owner.PostUsageAsync(setup.RaftId, Repair(new UsageLine(item), new UsageLine(await factory.ItemAsync(), quantity)))).InvalidFieldsAsync());
        }

        Assert.Equal(0, await factory.QueryAsync(db => db.MaterialUsages.CountAsync(u => u.RaftId == setup.RaftId)));
    }

    [Fact]
    public async Task An_unreadable_date_answers_400_with_msg_gen_01()
    {
        var setup = await factory.CycleAsync();
        var answer = await setup.Owner.Client.PostAsJsonAsync(
            $"/api/rafts/{setup.RaftId}/material-usages", new { usageDate = "26/10/2026", repairReason = "x", items = new[] { new UsageLine(await factory.ItemAsync()) } });

        Assert.Equal(HttpStatusCode.BadRequest, answer.StatusCode);
        Assert.Equal("MSG-GEN-01", await answer.ErrorCodeAsync());
    }

    [Theory]
    [InlineData(InputItemGroup.Seed)]
    [InlineData(InputItemGroup.Other)]
    public async Task Only_items_of_the_group_material_can_be_used(InputItemGroup group)
    {
        var setup = await factory.CycleAsync();
        var fields = await (await setup.Owner.Client.PostUsageAsync(setup.RaftId, Repair(new UsageLine(await factory.ItemAsync()), new UsageLine(await factory.ItemAsync(group))))).InvalidFieldsAsync();
        Assert.Equal(["Line 2 item"], fields);
        Assert.Equal(0, await factory.QueryAsync(db => db.MaterialUsages.CountAsync(u => u.RaftId == setup.RaftId)));
    }

    // POST-1: one record per item of a repair.
    [Fact]
    public async Task An_item_given_twice_in_one_repair_is_refused_but_a_later_repair_may_use_it_again()
    {
        var setup = await factory.CycleAsync();
        var item = await factory.ItemAsync();
        Assert.Equal(["Line 2 item"], await (await setup.Owner.Client.PostUsageAsync(setup.RaftId, Repair(new UsageLine(item), new UsageLine(item)))).InvalidFieldsAsync());

        await setup.Owner.Client.RecordUsageAsync(setup.RaftId, item);
        await setup.Owner.Client.RecordUsageAsync(setup.RaftId, item);
        Assert.Equal(2, await factory.QueryAsync(db => db.MaterialUsages.CountAsync(u => u.RaftId == setup.RaftId)));
    }

    // GBR-01: a discontinued item keeps its history and cannot be chosen.
    [Fact]
    public async Task A_discontinued_item_answers_msg_gen_09_naming_it()
    {
        var setup = await factory.CycleAsync();
        var item = MaterialUsageTestKit.NewItem(statusId: StatusIds.Usage.Discontinued);
        await factory.UseDbAsync(db => db.FarmingInputItems.Add(item));

        var refused = await (await setup.Owner.Client.PostUsageAsync(setup.RaftId, Repair(new UsageLine(await factory.ItemAsync()), new UsageLine(item.Id)))).ReadAsync(HttpStatusCode.BadRequest);

        Assert.Equal(("MSG-GEN-09", "Line 2 item"), (refused.Text("code"), refused.Text("field")));
        Assert.Contains(item.Code, refused.Text("detail"));
    }

    // BR-CYCLE-03: not later than today, and outside the closed cycles. The first and the last day of a closed cycle belong to it.
    [Fact]
    public async Task A_date_in_a_closed_cycle_answers_msg_gen_09_naming_the_cycle_and_the_days_around_it_are_free()
    {
        var setup = await factory.RaftAsync();
        var owner = setup.Owner.Client;
        var item = await factory.ItemAsync();
        var closed = await factory.AddCycleAsync(setup.RaftId, Today.AddDays(-30), Today.AddDays(-20));

        foreach (var date in new[] { Today.AddDays(-30), Today.AddDays(-25), Today.AddDays(-20) })
        {
            var refused = await (await owner.PostUsageAsync(setup.RaftId, new UsageBody(date, "x", [new UsageLine(item)]))).ReadAsync(HttpStatusCode.Conflict);
            Assert.Equal("MSG-GEN-09", refused.Text("code"));
            Assert.Equal(closed.Id, refused.GetProperty("cycleId").GetGuid());
            Assert.Contains($"{Today.AddDays(-30):dd/MM/yyyy} to {Today.AddDays(-20):dd/MM/yyyy}", refused.Text("detail"));
            Assert.Contains("Closed", refused.Text("detail"));
        }

        await owner.RecordUsageAsync(setup.RaftId, item, Today.AddDays(-31));
        await owner.RecordUsageAsync(setup.RaftId, item, Today.AddDays(-19));
        await owner.RecordUsageAsync(setup.RaftId, item, Today);
    }

    // A raft whose cycle was closed today can repair from tomorrow: today is still the closed cycle (BR-CYCLE-04).
    [Fact]
    public async Task Today_is_refused_once_the_open_cycle_was_closed_today()
    {
        var setup = await factory.CycleAsync();
        var owner = setup.Owner.Client;
        var item = await factory.ItemAsync();
        await owner.RecordUsageAsync(setup.RaftId, item);

        await factory.CloseCycleAsync(setup.CycleId);

        var refused = await (await owner.PostUsageAsync(setup.RaftId, new UsageBody(Today, "x", [new UsageLine(item)]))).ReadAsync(HttpStatusCode.Conflict);
        Assert.Equal("MSG-GEN-09", refused.Text("code"));
    }

    // GBR-01: read-only, also for the Owner, while the usage can still be viewed.
    [Fact]
    public async Task A_discontinued_raft_or_zone_refuses_a_record_with_msg_gen_09_and_keeps_its_usage_visible()
    {
        var setup = await factory.RaftAsync();
        var owner = setup.Owner.Client;
        var item = await factory.ItemAsync();
        var record = await owner.RecordUsageAsync(setup.RaftId, item);
        Assert.True((await owner.UsageListAsync(setup.RaftId)).Flag("canRecord"));

        await factory.DiscontinueRaftAsync(setup.RaftId);
        await AssertReadOnlyAsync("Raft ");

        await factory.UpdateAsync<Raft>(setup.RaftId, r => r.StatusId = StatusIds.Usage.InUse);
        await factory.UpdateAsync<FarmingZone>(setup.ZoneId, z => z.StatusId = StatusIds.Usage.Discontinued);
        await AssertReadOnlyAsync("Farming zone ");

        async Task AssertReadOnlyAsync(string named)
        {
            var refused = await (await owner.PostUsageAsync(setup.RaftId, Repair(new UsageLine(item)))).ReadAsync(HttpStatusCode.Conflict);
            Assert.Equal("MSG-GEN-09", refused.Text("code"));
            Assert.StartsWith(named, refused.Text("detail"));

            var list = await owner.UsageListAsync(setup.RaftId);
            Assert.False(list.Flag("canRecord"));
            Assert.False(list.GetProperty("groups")[0].GetProperty("repairs")[0].GetProperty("records")[0].Flag("canEdit"));
            Assert.False((await owner.GetUsageAsync(record.Id())).Flag("canEdit"));
        }
    }

    [Fact]
    public async Task An_unknown_raft_answers_404_on_the_list_and_the_record()
    {
        var owner = await factory.OwnerAsync();
        Assert.Equal(HttpStatusCode.NotFound, (await owner.Client.GetAsync($"/api/rafts/{Guid.NewGuid()}/material-usages")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await owner.Client.PostUsageAsync(Guid.NewGuid(), Repair(new UsageLine(Guid.NewGuid())))).StatusCode);
    }
}
