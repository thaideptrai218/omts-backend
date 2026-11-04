using Microsoft.EntityFrameworkCore;
using OMTS.Api.IntegrationTests.Cultivation;
using OMTS.Api.IntegrationTests.Farming;
using OMTS.Api.IntegrationTests.Infrastructure;
using OMTS.Domain.Entities.Cultivation;

namespace OMTS.Api.IntegrationTests.Harvest;

/// <summary>
/// HARVEST-01.2 on a correction and a removal: step 8 is the same on a correction (HARVEST-01.1 step 4), so marking the last seeded lane finished reminds. The marker <c>CloseRemindedAt</c> stops a second
/// reminder only while the lanes stay Harvested: a correction or removal that takes one back re-arms it, as the earlier reminder is no longer true.
/// </summary>
public class HarvestLotCorrectionReminderTests(AuthApiFactory factory) : IClassFixture<AuthApiFactory>
{
    private Task<int> RemindersAsync(Guid cycleId, Guid? account = null) => factory.QueryAsync(db =>
        db.Notifications.CountAsync(n => n.Type == "CYCLE_CLOSE_REMINDER" && n.RelatedRecordId == cycleId && (account == null || n.RecipientAccountId == account)));

    private Task<DateTimeOffset?> MarkerAsync(Guid cycleId) => factory.QueryAsync(db => db.CultivationCycles.Where(c => c.Id == cycleId).Select(c => c.CloseRemindedAt).SingleAsync());

    [Fact]
    public async Task A_correction_that_finishes_the_last_seeded_lane_reminds_the_managers_and_the_answer_says_it()
    {
        var raft = await factory.SeededRaftAsync(1, 2);
        var manager = await factory.ManagerAsync(raft.ZoneId);
        await manager.Client.HarvestLotAsync(raft.RaftId, HarvestFields.OfLanes(1).FinishedLanes(1));
        var lot = await manager.Client.HarvestLotAsync(raft.RaftId, HarvestFields.OfLanes(2));
        Assert.Null(await MarkerAsync(raft.CycleId));

        var saved = await factory.CorrectLotAsync(manager.Client, lot.Id(), HarvestFields.OfLanes(2).FinishedLanes(2));

        Assert.True(saved.GetProperty("everySeededLaneHarvested").GetBoolean());
        Assert.Equal(1, await RemindersAsync(raft.CycleId, manager.Account.Id));
        Assert.NotNull(await MarkerAsync(raft.CycleId));
    }

    [Fact]
    public async Task A_further_correction_with_every_lane_still_harvested_sends_no_second_reminder()
    {
        var raft = await factory.SeededRaftAsync(1);
        var manager = await factory.ManagerAsync(raft.ZoneId);
        var lot = await manager.Client.HarvestLotAsync(raft.RaftId, HarvestFields.OfLanes(1).FinishedLanes(1));
        Assert.Equal(1, await RemindersAsync(raft.CycleId, manager.Account.Id));

        var saved = await factory.CorrectLotAsync(manager.Client, lot.Id(), HarvestFields.OfLanes(1).FinishedLanes(1) with { Cages = "9" });

        Assert.True(saved.GetProperty("everySeededLaneHarvested").GetBoolean());
        Assert.Equal(1, await RemindersAsync(raft.CycleId, manager.Account.Id));
    }

    [Fact]
    public async Task A_correction_that_takes_a_lane_back_re_arms_the_reminder_for_the_real_last_lane()
    {
        var raft = await factory.SeededRaftAsync(1);
        var manager = await factory.ManagerAsync(raft.ZoneId);
        var lot = await manager.Client.HarvestLotAsync(raft.RaftId, HarvestFields.OfLanes(1).FinishedLanes(1));

        var saved = await factory.CorrectLotAsync(manager.Client, lot.Id(), HarvestFields.OfLanes(1));
        Assert.False(saved.GetProperty("everySeededLaneHarvested").GetBoolean());
        Assert.Null(await MarkerAsync(raft.CycleId));
        Assert.Equal(1, await RemindersAsync(raft.CycleId, manager.Account.Id));

        await manager.Client.HarvestLotAsync(raft.RaftId, HarvestFields.OfLanes(1).FinishedLanes(1));

        Assert.Equal(2, await RemindersAsync(raft.CycleId, manager.Account.Id));
    }

    [Fact]
    public async Task A_removal_that_takes_a_lane_back_re_arms_it_and_a_removal_that_does_not_keeps_the_marker()
    {
        var raft = await factory.SeededRaftAsync(1, 2);
        var manager = await factory.ManagerAsync(raft.ZoneId);
        var extra = await manager.Client.HarvestLotAsync(raft.RaftId, HarvestFields.OfLanes(1));
        var finishing = await manager.Client.HarvestLotAsync(raft.RaftId, HarvestFields.OfLanes(1, 2).FinishedLanes(1, 2));
        Assert.Equal(1, await RemindersAsync(raft.CycleId, manager.Account.Id));

        await factory.RemoveLotAsync(manager.Client, extra.Id());
        Assert.NotNull(await MarkerAsync(raft.CycleId));

        await factory.RemoveLotAsync(manager.Client, finishing.Id());
        Assert.Null(await MarkerAsync(raft.CycleId));

        await manager.Client.HarvestLotAsync(raft.RaftId, HarvestFields.OfLanes(1, 2).FinishedLanes(1, 2));
        Assert.Equal(2, await RemindersAsync(raft.CycleId, manager.Account.Id));
    }

    // A lane seeded after the reminder is the "once per cycle" call of H1a: a correction elsewhere must not re-arm what it did not change.
    [Fact]
    public async Task A_correction_with_a_lane_seeded_after_the_reminder_does_not_re_arm_it()
    {
        var raft = await factory.SeededRaftAsync(1);
        var owner = raft.Owner.Client;
        var manager = await factory.ManagerAsync(raft.ZoneId);
        var lot = await manager.Client.HarvestLotAsync(raft.RaftId, HarvestFields.OfLanes(1).FinishedLanes(1));
        await owner.RecordSeedingAsync(raft.CycleId, raft.Lot.Session([2]));

        await factory.CorrectLotAsync(manager.Client, lot.Id(), HarvestFields.OfLanes(1).FinishedLanes(1) with { Cages = "9" });

        Assert.NotNull(await MarkerAsync(raft.CycleId));
    }

    // An Owner is not told of their own action (OMTS-216); the answer still says the cycle can be closed.
    [Fact]
    public async Task An_owner_who_corrects_the_last_lane_is_not_reminded_but_the_managers_are()
    {
        var raft = await factory.SeededRaftAsync(1);
        var owner = raft.Owner;
        var manager = await factory.ManagerAsync(raft.ZoneId);
        var lot = await manager.Client.HarvestLotAsync(raft.RaftId, HarvestFields.OfLanes(1));

        var saved = await factory.CorrectLotAsync(owner.Client, lot.Id(), HarvestFields.OfLanes(1).FinishedLanes(1));

        Assert.True(saved.GetProperty("everySeededLaneHarvested").GetBoolean());
        Assert.Equal(0, await RemindersAsync(raft.CycleId, owner.Account.Id));
        Assert.Equal(1, await RemindersAsync(raft.CycleId, manager.Account.Id));
    }

    // Nothing is sent or marked when the save does not stand.
    [Fact]
    public async Task A_refused_correction_sends_no_reminder_and_sets_no_marker()
    {
        var raft = await factory.SeededRaftAsync(1);
        var manager = await factory.ManagerAsync(raft.ZoneId);
        var lot = await manager.Client.HarvestLotAsync(raft.RaftId, HarvestFields.OfLanes(1));

        var answer = await manager.Client.PutHarvestLotAsync(lot.Id(), new HarvestCorrection(HarvestFields.OfLanes(1).FinishedLanes(1), RowVersion: AcsTestKit.StaleRowVersion));

        Assert.Equal("MSG06", await answer.ErrorCodeAsync());
        Assert.Equal(0, await RemindersAsync(raft.CycleId));
        Assert.Null(await MarkerAsync(raft.CycleId));
    }
}
