using System.Net;
using Microsoft.EntityFrameworkCore;
using OMTS.Api.IntegrationTests.Farming;
using OMTS.Api.IntegrationTests.Infrastructure;
using OMTS.Domain.Constants;
using OMTS.Domain.Entities.Farming;
using OMTS.Domain.Entities.Purchasing;
using OMTS.Domain.Enums;
using static OMTS.Api.IntegrationTests.Farming.FarmingTestKit;

namespace OMTS.Api.IntegrationTests.Cultivation;

/// <summary>CYCLE-03.1, BR-CYCLE-03, BR-CYCLE-04: the reason for the edit, the lock of a closed cycle, the move to another raft, the version, the audit log.</summary>
public class MaterialUsageEditTests(AuthApiFactory factory) : IClassFixture<AuthApiFactory>
{
    [Fact]
    public async Task An_edit_changes_the_values_and_the_audit_log_keeps_the_reason_and_the_changed_fields_only()
    {
        var setup = await factory.CycleAsync();
        var owner = setup.Owner.Client;
        var manager = await factory.ManagerAsync(setup.ZoneId);
        var (buoy, rope) = (await factory.ItemAsync(), await factory.ItemAsync());
        var record = await owner.RecordUsageAsync(setup.RaftId, buoy, Today.AddDays(-3), 2m);

        // No own-record rule here: a Zone Manager of the zone edits what the Owner recorded (BR-CYCLE-03), and the record keeps who recorded it.
        var saved = await (await manager.Client.PutUsageAsync(record.Id(), record.AsEdit("  typed the wrong item ") with
        {
            UsageDate = Today.AddDays(-2), ItemId = rope, Quantity = 3.5m, RepairReason = "snapped rope",
        })).ReadAsync(HttpStatusCode.OK);

        Assert.Equal(("MSG-GEN-05", "Material usage saved."), (saved.Text("messageCode"), saved.Text("message")));
        var after = saved.GetProperty("record");
        Assert.Equal((Today.AddDays(-2), rope, 3.5m, "snapped rope"), (after.Date("usageDate"), after.GetProperty("itemId").GetGuid(), after.GetProperty("quantity").GetDecimal(), after.Text("repairReason")));
        Assert.Equal(setup.Owner.Account.Id, after.GetProperty("recordedByAccountId").GetGuid());

        var audit = await factory.QueryAsync(db => db.AuditEvents.SingleAsync(e => e.Action == "MaterialUsage.Updated" && e.RecordId == record.Id()));
        Assert.Equal("typed the wrong item", audit.Reason);
        Assert.Equal(("MaterialUsage", setup.ZoneId), (audit.RecordType, audit.ZoneId));
        Assert.Contains("broken buoy", audit.Before);
        Assert.Contains("snapped rope", audit.After);
        Assert.DoesNotContain("Raft", audit.After);
    }

    [Fact]
    public async Task An_edit_that_changes_nothing_answers_200_and_writes_no_audit_row_but_still_needs_the_reason()
    {
        var setup = await factory.CycleAsync();
        var record = await setup.Owner.Client.RecordUsageAsync(setup.RaftId, await factory.ItemAsync());

        Assert.Equal(["Reason"], await (await setup.Owner.Client.PutUsageAsync(record.Id(), record.AsEdit(null))).InvalidFieldsAsync());
        await (await setup.Owner.Client.PutUsageAsync(record.Id(), record.AsEdit())).ReadAsync(HttpStatusCode.OK);

        Assert.False(await factory.QueryAsync(db => db.AuditEvents.AnyAsync(e => e.Action == "MaterialUsage.Updated" && e.RecordId == record.Id())));
    }

    [Fact]
    public async Task Every_invalid_field_is_named_with_msg_gen_01()
    {
        var setup = await factory.CycleAsync();
        var owner = setup.Owner.Client;
        var record = await owner.RecordUsageAsync(setup.RaftId, await factory.ItemAsync());

        var all = new UsageEdit(null, null, null, null, " ", new string('x', 1001), record.Text("rowVersion"));
        Assert.Equal(["Raft", "Repair date", "Item", "Quantity", "Repair reason", "Reason"], await (await owner.PutUsageAsync(record.Id(), all)).InvalidFieldsAsync());

        Assert.Equal(["Raft"], await (await owner.PutUsageAsync(record.Id(), record.AsEdit() with { RaftId = Guid.NewGuid() })).InvalidFieldsAsync());
        Assert.Equal(["Repair date"], await (await owner.PutUsageAsync(record.Id(), record.AsEdit() with { UsageDate = Today.AddDays(1) })).InvalidFieldsAsync());
        Assert.Equal(["Item"], await (await owner.PutUsageAsync(record.Id(), record.AsEdit() with { ItemId = await factory.ItemAsync(InputItemGroup.Seed) })).InvalidFieldsAsync());
        Assert.Equal(["Quantity"], await (await owner.PutUsageAsync(record.Id(), record.AsEdit() with { Quantity = 0m })).InvalidFieldsAsync());
        Assert.Equal(["RowVersion"], await (await owner.PutUsageAsync(record.Id(), record.AsEdit() with { RowVersion = null })).InvalidFieldsAsync());
    }

    // NFR-09 excepts only logs and water measurements.
    [Fact]
    public async Task A_stale_version_answers_msg06_and_saves_nothing()
    {
        var setup = await factory.CycleAsync();
        var record = await setup.Owner.Client.RecordUsageAsync(setup.RaftId, await factory.ItemAsync(), quantity: 2m);

        var refused = await (await setup.Owner.Client.PutUsageAsync(record.Id(), record.AsEdit() with { Quantity = 9m, RowVersion = AcsTestKit.StaleRowVersion })).ReadAsync(HttpStatusCode.Conflict);

        Assert.Equal("MSG06", refused.Text("code"));
        Assert.Equal(2m, (await setup.Owner.Client.GetUsageAsync(record.Id())).GetProperty("quantity").GetDecimal());
    }

    // BR-CYCLE-04: once its cycle closes the record no longer changes, for the Owner too, before any field is read.
    [Fact]
    public async Task A_record_of_a_cycle_that_closed_is_locked()
    {
        var setup = await factory.CycleAsync();
        var owner = setup.Owner.Client;
        var record = await owner.RecordUsageAsync(setup.RaftId, await factory.ItemAsync(), Today.AddDays(-3));

        await factory.CloseCycleAsync(setup.CycleId);

        var refused = await (await owner.PutUsageAsync(record.Id(), record.AsEdit(null) with { Quantity = 0m })).ReadAsync(HttpStatusCode.Conflict);
        Assert.Equal(("MSG-GEN-09", setup.CycleId), (refused.Text("code"), refused.GetProperty("cycleId").GetGuid()));
        Assert.False((await owner.GetUsageAsync(record.Id())).Flag("canEdit"));
    }

    [Fact]
    public async Task A_record_cannot_be_moved_into_a_closed_cycle_by_its_date_or_its_raft()
    {
        var setup = await factory.RaftAsync();
        var owner = setup.Owner.Client;
        var closed = await factory.AddCycleAsync(setup.RaftId, Today.AddDays(-30), Today.AddDays(-20));
        var record = await owner.RecordUsageAsync(setup.RaftId, await factory.ItemAsync(), Today.AddDays(-5));

        var byDate = await (await owner.PutUsageAsync(record.Id(), record.AsEdit() with { UsageDate = Today.AddDays(-25) })).ReadAsync(HttpStatusCode.Conflict);
        Assert.Equal(("MSG-GEN-09", closed.Id), (byDate.Text("code"), byDate.GetProperty("cycleId").GetGuid()));

        var other = (await owner.CreateRaftAsync(setup.ZoneId)).Id();
        var otherClosed = await factory.AddCycleAsync(other, Today.AddDays(-6), Today.AddDays(-4));
        var byRaft = await (await owner.PutUsageAsync(record.Id(), record.AsEdit() with { RaftId = other })).ReadAsync(HttpStatusCode.Conflict);
        Assert.Equal(("MSG-GEN-09", otherClosed.Id), (byRaft.Text("code"), byRaft.GetProperty("cycleId").GetGuid()));
    }

    // BR-CYCLE-03: corrects a record entered on the wrong raft. The date is checked against the cycles of the new raft.
    [Fact]
    public async Task A_record_moves_to_another_raft_in_use_and_to_its_group_there()
    {
        var setup = await factory.CycleAsync();
        var owner = setup.Owner.Client;
        var record = await owner.RecordUsageAsync(setup.RaftId, await factory.ItemAsync());
        var other = await owner.CreateRaftAsync(setup.ZoneId);

        var moved = (await (await owner.PutUsageAsync(record.Id(), record.AsEdit("wrong raft") with { RaftId = other.Id() })).ReadAsync(HttpStatusCode.OK)).GetProperty("record");

        Assert.Equal(other.Id(), moved.GetProperty("raftId").GetGuid());
        Assert.Equal(other.Text("code"), moved.Text("raftCode"));
        Assert.True(moved.IsNull("cycleId"));
        Assert.Empty((await owner.UsageListAsync(setup.RaftId)).GroupsOf());
        Assert.Equal([record.Id()], (await owner.UsageListAsync(other.Id())).GroupsOf().Single().Records);

        var audit = await factory.QueryAsync(db => db.AuditEvents.SingleAsync(e => e.Action == "MaterialUsage.Updated" && e.RecordId == record.Id()));
        Assert.Equal(("wrong raft", other.Text("code")), (audit.Reason, audit.RecordCode));
        Assert.Contains(other.Text("code"), audit.After);
    }

    [Fact]
    public async Task A_move_needs_access_to_the_zone_of_the_other_raft_too()
    {
        var setup = await factory.CycleAsync();
        var owner = setup.Owner.Client;
        var record = await owner.RecordUsageAsync(setup.RaftId, await factory.ItemAsync());
        var otherRaft = (await owner.CreateRaftAsync((await owner.CreateZoneAsync()).Id())).Id();

        var manager = await factory.ManagerAsync(setup.ZoneId);
        var refused = await manager.Client.PutUsageAsync(record.Id(), record.AsEdit() with { RaftId = otherRaft });
        Assert.Equal(HttpStatusCode.Forbidden, refused.StatusCode);
        Assert.Equal("MSG-GEN-08", await refused.ErrorCodeAsync());
        Assert.Equal(setup.RaftId, (await owner.GetUsageAsync(record.Id())).GetProperty("raftId").GetGuid());

        var allZones = await factory.StaffAsync(null);
        await (await allZones.Client.PutUsageAsync(record.Id(), record.AsEdit() with { RaftId = otherRaft })).ReadAsync(HttpStatusCode.OK);
    }

    // GBR-01: neither end of an edit may be a read-only raft.
    [Fact]
    public async Task A_discontinued_raft_or_zone_at_either_end_answers_msg_gen_09()
    {
        var setup = await factory.CycleAsync();
        var owner = setup.Owner.Client;
        var record = await owner.RecordUsageAsync(setup.RaftId, await factory.ItemAsync());
        var other = (await owner.CreateRaftAsync(setup.ZoneId)).Id();

        await factory.DiscontinueRaftAsync(other);
        var toDiscontinued = await (await owner.PutUsageAsync(record.Id(), record.AsEdit() with { RaftId = other })).ReadAsync(HttpStatusCode.Conflict);
        Assert.Equal("MSG-GEN-09", toDiscontinued.Text("code"));

        await factory.DiscontinueRaftAsync(setup.RaftId);
        var fromDiscontinued = await (await owner.PutUsageAsync(record.Id(), record.AsEdit())).ReadAsync(HttpStatusCode.Conflict);
        Assert.Equal("MSG-GEN-09", fromDiscontinued.Text("code"));

        await factory.UpdateAsync<Raft>(setup.RaftId, r => r.StatusId = StatusIds.Usage.InUse);
        await factory.UpdateAsync<FarmingZone>(setup.ZoneId, z => z.StatusId = StatusIds.Usage.Discontinued);
        var inDiscontinuedZone = await (await owner.PutUsageAsync(record.Id(), record.AsEdit())).ReadAsync(HttpStatusCode.Conflict);
        Assert.Equal("MSG-GEN-09", inDiscontinuedZone.Text("code"));
    }

    // GBR-01: only a new choice must be In Use, so a record on an item discontinued since can still be corrected.
    [Fact]
    public async Task A_discontinued_item_can_stay_on_a_record_but_not_be_chosen()
    {
        var setup = await factory.CycleAsync();
        var owner = setup.Owner.Client;
        var item = await factory.ItemAsync();
        var record = await owner.RecordUsageAsync(setup.RaftId, item);

        await factory.UpdateAsync<FarmingInputItem>(item, i => i.StatusId = StatusIds.Usage.Discontinued);
        var kept = await (await owner.PutUsageAsync(record.Id(), record.AsEdit() with { Quantity = 5m })).ReadAsync(HttpStatusCode.OK);
        Assert.Equal(5m, kept.GetProperty("record").GetProperty("quantity").GetDecimal());

        var discontinued = await factory.ItemAsync(statusId: StatusIds.Usage.Discontinued);
        var refused = await (await owner.PutUsageAsync(record.Id(), kept.GetProperty("record").AsEdit() with { ItemId = discontinued })).ReadAsync(HttpStatusCode.BadRequest);
        Assert.Equal(("MSG-GEN-09", "Item"), (refused.Text("code"), refused.Text("field")));
    }

    [Fact]
    public async Task An_unknown_record_answers_404()
    {
        var owner = await factory.OwnerAsync();
        Assert.Equal(HttpStatusCode.NotFound, (await owner.Client.GetAsync($"/api/material-usages/{Guid.NewGuid()}")).StatusCode);
        var edit = new UsageEdit(Guid.NewGuid(), Today, Guid.NewGuid(), 1m, "x", "y", "");
        Assert.Equal(HttpStatusCode.NotFound, (await owner.Client.PutUsageAsync(Guid.NewGuid(), edit)).StatusCode);
    }
}
