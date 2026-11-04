using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using OMTS.Api.IntegrationTests.Farming;
using OMTS.Api.IntegrationTests.Infrastructure;
using OMTS.Domain.Constants;
using OMTS.Domain.Entities.Farming;
using OMTS.Domain.Enums;
using static OMTS.Api.IntegrationTests.Farming.FarmingTestKit;
using static OMTS.Api.IntegrationTests.Infrastructure.AcsTestKit;

namespace OMTS.Api.IntegrationTests.Cultivation;

/// <summary>QA of §I.4.4, GFR-02 and BR-FARM-08 on PR C3: every route for every kind of caller, a move across zones with both audit sides, and the item reads.</summary>
public class MaterialUsagesQaTestsAccess(AuthApiFactory factory) : IClassFixture<AuthApiFactory>
{
    private static IEnumerable<Func<Task<HttpResponseMessage>>> EveryRoute(HttpClient client, Guid raftId, Guid itemId, JsonElement record) =>
    [
        () => client.GetAsync($"/api/rafts/{raftId}/material-usages"),
        () => client.PostUsageAsync(raftId, new UsageBody(Today, "no access", [new UsageLine(itemId)])),
        () => client.GetAsync($"/api/material-usages/{record.Id()}"),
        () => client.PutUsageAsync(record.Id(), record.AsEdit() with { Quantity = 9m }),
    ];

    private static async Task AssertRefusedEverywhereAsync(HttpClient client, Guid raftId, Guid itemId, JsonElement record)
    {
        foreach (var call in EveryRoute(client, raftId, itemId, record))
        {
            var answer = await call();
            Assert.Equal(HttpStatusCode.Forbidden, answer.StatusCode);
            Assert.Equal("MSG-GEN-08", await answer.ErrorCodeAsync());
        }
    }

    private async Task<HttpClient> StaffWithPairsAsync(params (StaffRole Role, Guid? ZoneId)[] pairs)
    {
        var password = NewPassword();
        var staff = await factory.AddAccountAsync(NewCode(), password, SubjectType.Staff);
        foreach (var (role, zoneId) in pairs)
        {
            await factory.GrantAsync(staff, role, zoneId is { } id ? await factory.ZoneEntityAsync(id) : null);
        }

        return await factory.LoginAsync(staff.Username, password);
    }

    [Fact]
    public async Task A_trader_is_refused_on_every_route_and_saves_nothing()
    {
        var setup = await factory.CycleAsync();
        var item = await factory.ItemAsync();
        var record = await setup.Owner.Client.RecordUsageAsync(setup.RaftId, item);

        await AssertRefusedEverywhereAsync(await factory.TraderAsync(), setup.RaftId, item, record);

        Assert.Equal(2m, (await setup.Owner.Client.GetUsageAsync(record.Id())).GetProperty("quantity").GetDecimal());
        Assert.Equal(1, await factory.QueryAsync(db => db.MaterialUsages.CountAsync(u => u.RaftId == setup.RaftId)));
    }

    // BR-FARM-08: each role is limited to the zones of its pairs, so another role's pair opens nothing in this zone.
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task A_manager_of_zone_b_who_is_accountant_here_or_of_all_zones_is_refused_here_and_works_in_b(bool accountantOfAllZones)
    {
        var setup = await factory.CycleAsync();
        var owner = setup.Owner.Client;
        var item = await factory.ItemAsync();
        var record = await owner.RecordUsageAsync(setup.RaftId, item);
        var zoneB = (await owner.CreateZoneAsync()).Id();
        var raftB = (await owner.CreateRaftAsync(zoneB)).Id();

        var mixed = await StaffWithPairsAsync((StaffRole.ZoneManager, zoneB), (StaffRole.Accountant, accountantOfAllZones ? null : setup.ZoneId));

        await AssertRefusedEverywhereAsync(mixed, setup.RaftId, item, record);
        Assert.True((await mixed.UsageListAsync(raftB)).Flag("canRecord"));
        await mixed.RecordUsageAsync(raftB, item);
    }

    // An Accountant pair in the zone of the other raft is no access to move a record there, whichever way the move goes.
    [Fact]
    public async Task A_mixed_pair_cannot_move_a_record_into_or_out_of_the_zone_where_the_person_is_only_accountant()
    {
        var setup = await factory.CycleAsync();
        var owner = setup.Owner.Client;
        var item = await factory.ItemAsync();
        var zoneB = (await owner.CreateZoneAsync()).Id();
        var raftB = (await owner.CreateRaftAsync(zoneB)).Id();
        var inA = await owner.RecordUsageAsync(setup.RaftId, item);
        var inB = await owner.RecordUsageAsync(raftB, item);

        var mixed = await StaffWithPairsAsync((StaffRole.ZoneManager, setup.ZoneId), (StaffRole.Accountant, zoneB));

        var into = await mixed.PutUsageAsync(inA.Id(), inA.AsEdit() with { RaftId = raftB });
        Assert.Equal(HttpStatusCode.Forbidden, into.StatusCode);
        var outOf = await mixed.PutUsageAsync(inB.Id(), inB.AsEdit() with { RaftId = setup.RaftId });
        Assert.Equal(HttpStatusCode.Forbidden, outOf.StatusCode);
        Assert.Equal(setup.RaftId, (await owner.GetUsageAsync(inA.Id())).GetProperty("raftId").GetGuid());
        Assert.Equal(raftB, (await owner.GetUsageAsync(inB.Id())).GetProperty("raftId").GetGuid());
    }

    // GFR-02: a revoke applies from the next action, for a record the person made too; a new pair gives the access back; the record keeps who recorded it.
    [Fact]
    public async Task After_a_revoke_the_recorder_is_refused_from_the_next_action_and_a_new_pair_gives_access_back()
    {
        var setup = await factory.CycleAsync();
        var owner = setup.Owner.Client;
        var item = await factory.ItemAsync();
        var manager = await factory.ManagerAsync(setup.ZoneId);
        var zoneB = await factory.ZoneEntityAsync((await owner.CreateZoneAsync()).Id());
        await factory.GrantAsync(manager.Account, StaffRole.ZoneManager, zoneB);
        var record = await manager.Client.RecordUsageAsync(setup.RaftId, item);

        var revoked = await owner.PostAsJsonAsync($"/api/accounts/{manager.Account.Id}/grants/{manager.Grant!.Id}/revoke", new { reason = "Moved" });
        Assert.True(revoked.IsSuccessStatusCode, await revoked.Content.ReadAsStringAsync());

        await AssertRefusedEverywhereAsync(manager.Client, setup.RaftId, item, record);
        var kept = await owner.GetUsageAsync(record.Id());
        Assert.Equal(manager.Account.Id, kept.GetProperty("recordedByAccountId").GetGuid());
        Assert.False(string.IsNullOrEmpty(kept.Text("recordedByName")));

        await factory.GrantAsync(manager.Account, StaffRole.ZoneManager, await factory.ZoneEntityAsync(setup.ZoneId));
        Assert.True((await manager.Client.GetUsageAsync(record.Id())).Flag("canEdit"));
    }

    // BR-FARM-08: the pairs of a discontinued zone stay Effective, for its history; GBR-01: nothing can be written.
    [Fact]
    public async Task A_manager_of_a_discontinued_zone_still_reads_and_cannot_write()
    {
        var setup = await factory.RaftAsync();
        var manager = await factory.ManagerAsync(setup.ZoneId);
        var item = await factory.ItemAsync();
        var record = await manager.Client.RecordUsageAsync(setup.RaftId, item);

        await factory.DiscontinueRaftAsync(setup.RaftId);
        await factory.UpdateAsync<FarmingZone>(setup.ZoneId, z => z.StatusId = StatusIds.Usage.Discontinued);

        var list = await manager.Client.UsageListAsync(setup.RaftId);
        Assert.False(list.Flag("canRecord"));
        Assert.False((await manager.Client.GetUsageAsync(record.Id())).Flag("canEdit"));
        var post = await manager.Client.PostUsageAsync(setup.RaftId, new UsageBody(Today, "x", [new UsageLine(item)]));
        Assert.Equal("MSG-GEN-09", (await post.ReadAsync(HttpStatusCode.Conflict)).Text("code"));
        Assert.Equal("MSG-GEN-09", (await (await manager.Client.PutUsageAsync(record.Id(), record.AsEdit())).ReadAsync(HttpStatusCode.Conflict)).Text("code"));
    }

    // BR-CYCLE-03 move across zones: the manager of both zones may; each zone's manager then sees exactly what is in their zone.
    [Fact]
    public async Task After_a_move_across_zones_the_old_zone_loses_the_record_and_the_new_zone_gets_it_on_every_route()
    {
        var setup = await factory.CycleAsync();
        var owner = setup.Owner.Client;
        var item = await factory.ItemAsync();
        var zoneB = (await owner.CreateZoneAsync()).Id();
        var raftB = await owner.CreateRaftAsync(zoneB);
        var cycleB = await owner.StartCycleAsync(raftB.Id(), new CycleBody(Today.AddDays(-20)));
        var (managerA, managerB) = (await factory.ManagerAsync(setup.ZoneId), await factory.ManagerAsync(zoneB));
        var record = await managerA.Client.RecordUsageAsync(setup.RaftId, item, Today.AddDays(-1));
        var both = await StaffWithPairsAsync((StaffRole.ZoneManager, setup.ZoneId), (StaffRole.ZoneManager, zoneB));

        var moved = (await (await both.PutUsageAsync(record.Id(), record.AsEdit("wrong raft") with { RaftId = raftB.Id() })).ReadAsync(HttpStatusCode.OK)).GetProperty("record");

        Assert.Equal(cycleB.Id(), moved.GetProperty("cycleId").GetGuid());
        Assert.True(moved.Flag("canEdit"));
        Assert.Equal(HttpStatusCode.Forbidden, (await managerA.Client.GetAsync($"/api/material-usages/{record.Id()}")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await managerA.Client.PutUsageAsync(record.Id(), moved.AsEdit() with { RaftId = setup.RaftId })).StatusCode);
        Assert.Empty((await managerA.Client.UsageListAsync(setup.RaftId)).GroupsOf());
        Assert.Equal(moved.Text("rowVersion"), (await managerB.Client.GetUsageAsync(record.Id())).Text("rowVersion"));
        Assert.Equal([record.Id()], (await managerB.Client.UsageListAsync(raftB.Id())).GroupsOf().Single().Records);
        Assert.Equal(managerA.Account.Id, moved.GetProperty("recordedByAccountId").GetGuid());
    }

    // GFR-02: a refused action shows MSG-GEN-08 and nothing of the record.
    [Fact]
    public async Task A_refusal_to_a_manager_of_another_zone_says_nothing_of_the_record()
    {
        var setup = await factory.CycleAsync();
        var owner = setup.Owner.Client;
        var item = await factory.ItemAsync();
        var record = await owner.RecordUsageAsync(setup.RaftId, item, reason: "secret repair reason");
        var other = await factory.ManagerAsync((await owner.CreateZoneAsync()).Id());

        foreach (var answer in new[]
        {
            await other.Client.GetAsync($"/api/material-usages/{record.Id()}"),
            await other.Client.GetAsync($"/api/rafts/{setup.RaftId}/material-usages"),
            await other.Client.PutUsageAsync(record.Id(), record.AsEdit()),
        })
        {
            var text = await answer.Content.ReadAsStringAsync();
            Assert.Equal(HttpStatusCode.Forbidden, answer.StatusCode);
            Assert.DoesNotContain("secret repair reason", text);
            Assert.DoesNotContain(record.Text("raftCode"), text);
            Assert.DoesNotContain(setup.CycleId.ToString(), text);
        }
    }

    // GBR-02 and the client list (§3): what the log holds of a move. Pinned, as what the old zone sees is a question for the PM (see the report, P3).
    [Fact]
    public async Task A_move_writes_one_row_in_the_new_zone_with_the_old_raft_in_before_and_the_old_zone_keeps_only_the_creation()
    {
        var setup = await factory.CycleAsync();
        var owner = setup.Owner.Client;
        var zoneB = (await owner.CreateZoneAsync()).Id();
        var raftA = await owner.GetFromJsonAsync<JsonElement>($"/api/rafts/{setup.RaftId}");
        var raftB = await owner.CreateRaftAsync(zoneB);
        var record = await owner.RecordUsageAsync(setup.RaftId, await factory.ItemAsync());

        await (await owner.PutUsageAsync(record.Id(), record.AsEdit("wrong raft") with { RaftId = raftB.Id() })).ReadAsync(HttpStatusCode.OK);

        var rows = await factory.QueryAsync(db => db.AuditEvents.Where(e => e.RecordId == record.Id()).OrderBy(e => e.OccurredAt).ToListAsync());
        Assert.Equal(["MaterialUsage.Created", "MaterialUsage.Updated"], rows.Select(e => e.Action));
        var (created, updated) = (rows[0], rows[1]);
        Assert.Equal((setup.ZoneId, raftA.Text("code")), (created.ZoneId!.Value, created.RecordCode));
        Assert.Equal((zoneB, raftB.Text("code"), "wrong raft"), (updated.ZoneId!.Value, updated.RecordCode, updated.Reason));
        Assert.Equal(setup.Owner.Account.Id, updated.ActorAccountId);
        Assert.Equal(raftA.Text("code"), JsonDocument.Parse(updated.Before!).RootElement.GetProperty("Raft").GetString());
        Assert.Equal(raftB.Text("code"), JsonDocument.Parse(updated.After!).RootElement.GetProperty("Raft").GetString());

        // By the zone or the raft code of the old side, a reader of the log finds the record created there and nothing of its leaving.
        Assert.False(await factory.QueryAsync(db => db.AuditEvents.AnyAsync(e => e.Action == "MaterialUsage.Updated" && e.ZoneId == setup.ZoneId)));
        Assert.False(await factory.QueryAsync(db => db.AuditEvents.AnyAsync(e => e.Action == "MaterialUsage.Updated" && e.RecordCode == raftA.Text("code"))));
    }

    // CYCLE-03.0 step 3 and BR-FARM-08: items have no zone. The reads a Zone Manager needs show no money, and a role with nothing to pick from is refused.
    [Fact]
    public async Task Item_reads_are_open_to_a_zone_manager_with_no_money_in_them_and_closed_to_a_trader_and_a_revoked_pair()
    {
        var setup = await factory.CycleAsync();
        var item = await factory.ItemAsync();
        var manager = await factory.ManagerAsync(setup.ZoneId);

        var listed = await manager.Client.GetFromJsonAsync<JsonElement>($"/api/farming-input-items?group=RaftMaterial&statusId={StatusIds.Usage.InUse}");
        var one = await manager.Client.GetFromJsonAsync<JsonElement>($"/api/farming-input-items/{item}");
        foreach (var view in listed.EnumerateArray().Append(one))
        {
            Assert.DoesNotContain(view.EnumerateObject(), p =>
                p.Name.Contains("price", StringComparison.OrdinalIgnoreCase) || p.Name.Contains("cost", StringComparison.OrdinalIgnoreCase) || p.Name.Contains("amount", StringComparison.OrdinalIgnoreCase));
        }

        var trader = await factory.TraderAsync();
        Assert.Equal(HttpStatusCode.Forbidden, (await trader.GetAsync("/api/farming-input-items")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await trader.GetAsync($"/api/farming-input-items/{item}")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await factory.CreateClient().GetAsync("/api/farming-input-items")).StatusCode);

        var revoked = await setup.Owner.Client.PostAsJsonAsync($"/api/accounts/{manager.Account.Id}/grants/{manager.Grant!.Id}/revoke", new { reason = "Moved" });
        Assert.True(revoked.IsSuccessStatusCode, await revoked.Content.ReadAsStringAsync());
        Assert.False((await manager.Client.GetAsync($"/api/farming-input-items/{item}")).IsSuccessStatusCode);
    }

    // BR-FARM-08: All zones also covers zones created later.
    [Fact]
    public async Task An_all_zones_manager_works_in_a_zone_created_later()
    {
        var setup = await factory.CycleAsync();
        var owner = setup.Owner.Client;
        var allZones = await factory.StaffAsync(null);
        var later = (await owner.CreateRaftAsync((await owner.CreateZoneAsync()).Id())).Id();
        var item = await factory.ItemAsync();

        var record = await allZones.Client.RecordUsageAsync(later, item);

        Assert.True((await allZones.Client.GetUsageAsync(record.Id())).Flag("canEdit"));
        await (await allZones.Client.PutUsageAsync(record.Id(), record.AsEdit() with { RaftId = setup.RaftId })).ReadAsync(HttpStatusCode.OK);
    }
}
