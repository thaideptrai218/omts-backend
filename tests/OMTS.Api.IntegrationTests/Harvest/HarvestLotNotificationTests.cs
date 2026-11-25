using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using OMTS.Api.IntegrationTests.Cultivation;
using OMTS.Api.IntegrationTests.Farming;
using OMTS.Api.IntegrationTests.Infrastructure;
using OMTS.Application.Features.Notifications;
using OMTS.Domain.Constants;
using OMTS.Domain.Entities.Farming;
using OMTS.Domain.Enums;
using OMTS.Infrastructure.Messaging.Fakes;
using static OMTS.Api.IntegrationTests.Farming.FarmingTestKit;

namespace OMTS.Api.IntegrationTests.Harvest;

/// <summary>MSG-HARVEST-01 to the Owner, a normal type, and MSG-CYCLE-08 (AC-13, BR-CYCLE-07), mandatory (BR-ACCOUNT-11). An Owner is not told of their own harvest (PM decision OMTS-216).</summary>
public class HarvestLotNotificationTests
{
    private const string LotType = "HARVEST_LOT_RECORDED";
    private const string ReminderType = "CYCLE_CLOSE_REMINDER";

    [Fact]
    public async Task A_lot_of_a_zone_manager_tells_every_owner_once_with_the_srs_text_and_not_the_manager()
    {
        using var factory = new AuthApiFactory();
        var raft = await factory.SeededRaftAsync();
        var otherOwner = await factory.OwnerAsync();
        var manager = await factory.ManagerAsync(raft.ZoneId);

        var lot = await manager.Client.HarvestLotAsync(raft.RaftId, HarvestFields.OfLanes(1) with { EstimatedKg = "385.5" });

        var rows = await factory.NoticesAsync(LotType, lot.Id());
        Assert.Equal(new[] { raft.Owner.Account.Id, otherOwner.Account.Id }.Order(), rows.Select(n => n.RecipientAccountId!.Value).Order());
        var code = lot.Text("lotCode");
        var raftCode = lot.Text("raftCode");
        var date = lot.Date("harvestDate");
        var planCode = lot.GetProperty("harvestPlan").Text("code");
        Assert.All(rows, n =>
        {
            Assert.Equal((LotType, "MSG-HARVEST-01", false), (n.Type, n.MessageCode, n.IsMandatory));
            Assert.Equal($"Raft {raftCode}: the harvest of {date:dd/MM/yyyy} is recorded as lot {code} (8 cages, about 385.5 kg) from harvest plan {planCode}.", n.Body);
            Assert.Equal(("HarvestLot", code), (n.RelatedRecordType, n.RelatedRecordCode));
            using var parameters = JsonDocument.Parse(n.MessageParams!);
            Assert.Equal((raftCode, date.ToString("yyyy-MM-dd"), code), (parameters.RootElement.Text("raft_code"), parameters.RootElement.Text("harvest_date"), parameters.RootElement.Text("lot_code")));
            Assert.Equal((8, 385.5m), (parameters.RootElement.GetProperty("cage_count").GetInt32(), parameters.RootElement.GetProperty("estimated_kg").GetDecimal()));
        });
    }

    [Fact]
    public async Task An_owner_is_not_told_of_their_own_lot_and_another_owner_is()
    {
        using var factory = new AuthApiFactory();
        var raft = await factory.SeededRaftAsync();
        var other = await factory.OwnerAsync();

        var lot = await raft.Owner.Client.HarvestLotAsync(raft.RaftId);

        var told = (await factory.NoticesAsync(LotType, lot.Id())).Select(n => n.RecipientAccountId!.Value).ToList();
        Assert.DoesNotContain(raft.Owner.Account.Id, told);
        Assert.Contains(other.Account.Id, told);
    }

    // AC-13: lanes 1 to 9 Harvested, lane 10 Growing: nothing yet. Lane 10 finished: the Zone Managers of the zone receive it.
    [Fact]
    public async Task Ac_13_the_last_seeded_lane_finished_reminds_every_zone_manager_of_the_zone_and_all_zones_and_is_mandatory()
    {
        using var factory = new AuthApiFactory();
        var setup = await factory.OwnerAsync();
        var zoneId = (await setup.Client.CreateZoneAsync()).Id();
        var raftBody = NewRaftBody(lanes: 10);
        var raft = await setup.Client.CreateRaftAsync(zoneId, raftBody);
        var cycle = await setup.Client.StartCycleAsync(raft.Id(), new CycleBody(Today.AddDays(-10)));
        var seed = await setup.Client.SeedLotAsync();
        await setup.Client.RecordSeedingAsync(cycle.Id(), seed.Session(Enumerable.Range(1, 10).ToList()));
        var manager = await factory.ManagerAsync(zoneId);
        var second = await factory.ManagerAsync(zoneId);
        var allZones = await factory.StaffAsync(null);
        var elsewhere = await factory.ManagerAsync((await setup.Client.CreateZoneAsync()).Id());
        var accountant = await factory.StaffAsync(null, StaffRole.Accountant);

        var nine = await manager.Client.RecordHarvestAsync(raft.Id(), new HarvestFields().FinishedLanes(1, 2, 3, 4, 5, 6, 7, 8, 9));
        Assert.False(nine.GetProperty("everySeededLaneHarvested").GetBoolean());
        Assert.Empty(await factory.NoticesAsync(ReminderType, cycle.Id()));

        var last = await manager.Client.FinishLanesAsync(raft.Id(), 10);

        Assert.True(last.GetProperty("everySeededLaneHarvested").GetBoolean());
        var rows = await factory.NoticesAsync(ReminderType, cycle.Id());
        Assert.Equal(new[] { manager.Account.Id, second.Account.Id, allZones.Account.Id }.Order(), rows.Select(n => n.RecipientAccountId!.Value).Order());
        var raftCode = raft.Text("code");
        Assert.All(rows, n =>
        {
            Assert.Equal(("MSG-CYCLE-08", true), (n.MessageCode, n.IsMandatory));
            Assert.Equal($"Raft {raftCode}: every seeded lane is harvested. Please close its cultivation cycle.", n.Body);
            Assert.Equal(("CultivationCycle", cycle.Id(), raftCode), (n.RelatedRecordType, n.RelatedRecordId, n.RelatedRecordCode));
            using var parameters = JsonDocument.Parse(n.MessageParams!);
            Assert.Equal(raftCode, parameters.RootElement.Text("raft_code"));
        });
    }

    [Fact]
    public async Task With_no_zone_manager_every_owner_gets_it_but_the_owner_who_recorded_it_and_the_answer_tells_them()
    {
        using var factory = new AuthApiFactory();
        var raft = await factory.SeededRaftAsync(1);
        var other = await factory.OwnerAsync();

        var saved = await raft.Owner.Client.RecordHarvestAsync(raft.RaftId, HarvestFields.OfLanes(1).FinishedLanes(1));

        Assert.True(saved.GetProperty("everySeededLaneHarvested").GetBoolean());
        var told = (await factory.NoticesAsync(ReminderType, raft.CycleId)).Select(n => n.RecipientAccountId!.Value).ToList();
        Assert.Contains(other.Account.Id, told);
        Assert.DoesNotContain(raft.Owner.Account.Id, told);
    }

    // An Owner finishing the last lane reminds the Zone Managers; a revoked or locked manager does not count, so the Owners get it.
    [Fact]
    public async Task An_owner_finishing_the_last_lane_reminds_the_managers_and_a_locked_manager_leaves_it_to_the_owners()
    {
        using var factory = new AuthApiFactory();
        var raft = await factory.SeededRaftAsync(1);
        var manager = await factory.ManagerAsync(raft.ZoneId);
        await raft.Owner.Client.RecordHarvestAsync(raft.RaftId, HarvestFields.OfLanes(1).FinishedLanes(1));
        Assert.Contains(await factory.NoticesAsync(ReminderType, raft.CycleId), n => n.RecipientAccountId == manager.Account.Id);

        var other = await factory.SeededRaftAsync(1);
        var locked = await factory.ManagerAsync(other.ZoneId);
        await factory.UpdateAsync<OMTS.Domain.Entities.Identity.Account>(locked.Account.Id, a => a.StatusId = StatusIds.Account.Locked);
        var second = await factory.OwnerAsync();
        await other.Owner.Client.RecordHarvestAsync(other.RaftId, HarvestFields.OfLanes(1).FinishedLanes(1));

        var told = (await factory.NoticesAsync(ReminderType, other.CycleId)).Select(n => n.RecipientAccountId!.Value).ToList();
        Assert.DoesNotContain(locked.Account.Id, told);
        Assert.Contains(second.Account.Id, told);
    }

    // BR-CYCLE-07: "every seeded lane": a lane with no seed does not hold it back, a Removed lane does not count.
    [Fact]
    public async Task A_lane_with_no_seed_does_not_hold_the_reminder_back_and_a_lot_not_finishing_the_last_lane_sends_none()
    {
        using var factory = new AuthApiFactory();
        var raft = await factory.SeededRaftAsync(1, 2);
        var manager = await factory.ManagerAsync(raft.ZoneId);

        await manager.Client.RecordHarvestAsync(raft.RaftId, new HarvestFields().FinishedLanes(1));
        await manager.Client.RecordHarvestAsync(raft.RaftId);
        Assert.Empty(await factory.NoticesAsync(ReminderType, raft.CycleId));

        await manager.Client.FinishLanesAsync(raft.RaftId, 2);
        Assert.Single(await factory.NoticesAsync(ReminderType, raft.CycleId), n => n.RecipientAccountId == manager.Account.Id);
    }

    // One reminder per cycle (design Q7): a lane seeded later and finished sends no second one, while the answer still says so.
    [Fact]
    public async Task A_lane_seeded_after_the_reminder_and_finished_sends_no_second_one()
    {
        using var factory = new AuthApiFactory();
        var raft = await factory.SeededRaftAsync(1);
        var manager = await factory.ManagerAsync(raft.ZoneId);
        await manager.Client.RecordHarvestAsync(raft.RaftId, HarvestFields.OfLanes(1).FinishedLanes(1));
        var count = (await factory.NoticesAsync(ReminderType, raft.CycleId)).Count;

        await raft.Owner.Client.RecordSeedingAsync(raft.CycleId, raft.Lot.Session([2]));
        await manager.Client.RecordHarvestAsync(raft.RaftId);
        var again = await manager.Client.FinishLanesAsync(raft.RaftId, 2);

        Assert.True(again.GetProperty("everySeededLaneHarvested").GetBoolean());
        Assert.Equal(count, (await factory.NoticesAsync(ReminderType, raft.CycleId)).Count);
    }

    // ACCOUNT-08: both open their records with the zone checked again (BR-ACCOUNT-12).
    [Fact]
    public async Task The_notifications_open_their_records_with_the_zone_and_a_lost_pair_cannot()
    {
        using var factory = new AuthApiFactory();
        var raft = await factory.SeededRaftAsync(1);
        var manager = await factory.ManagerAsync(raft.ZoneId);
        var lot = await manager.Client.HarvestLotAsync(raft.RaftId, HarvestFields.OfLanes(1).FinishedLanes(1));

        var ownerRow = (await factory.NoticesAsync(LotType, lot.Id())).Single(n => n.RecipientAccountId == raft.Owner.Account.Id);
        var opened = await (await raft.Owner.Client.GetAsync($"/api/me/notifications/{ownerRow.Id}/open")).ReadAsync(HttpStatusCode.OK);
        Assert.Equal(("HarvestLot", lot.Id(), lot.Text("lotCode")), (opened.Text("recordType"), opened.GetProperty("recordId").GetGuid(), opened.Text("recordCode")));
        Assert.Equal(raft.ZoneId, opened.GetProperty("zoneId").GetGuid());

        var managerRow = (await factory.NoticesAsync(ReminderType, raft.CycleId)).Single(n => n.RecipientAccountId == manager.Account.Id);
        Assert.Equal(HttpStatusCode.OK, (await manager.Client.GetAsync($"/api/me/notifications/{managerRow.Id}/open")).StatusCode);

        await factory.UpdateAsync<OMTS.Domain.Entities.Identity.AccessGrant>(manager.Grant!.Id, g => { g.StatusId = StatusIds.AccessGrant.Revoked; g.RevokedAt = DateTimeOffset.UtcNow; });
        Assert.Equal(HttpStatusCode.Forbidden, (await manager.Client.GetAsync($"/api/me/notifications/{managerRow.Id}/open")).StatusCode);
    }

    [Fact]
    public void The_types_go_to_the_roles_that_receive_them()
    {
        var lot = NotificationTypes.Find(LotType)!;
        var reminder = NotificationTypes.Find(ReminderType)!;

        Assert.Equal([NotificationRole.CooperativeOwner], lot.Roles);
        Assert.Equal([NotificationRole.CooperativeOwner, NotificationRole.ZoneManager], reminder.Roles);
    }
}
