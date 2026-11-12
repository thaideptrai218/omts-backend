using System.Net;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using OMTS.Api.IntegrationTests.Cultivation;
using OMTS.Api.IntegrationTests.Farming;
using OMTS.Api.IntegrationTests.Infrastructure;
using OMTS.Domain.Entities.Cultivation;
using OMTS.Domain.Entities.Harvest;
using OMTS.Domain.Entities.Platform;
using OMTS.Infrastructure.Messaging.Fakes;
using static OMTS.Api.IntegrationTests.Farming.FarmingTestKit;

namespace OMTS.Api.IntegrationTests.Harvest;

/// <summary>QA of PR H1b: GBR-02 on a correction and a removal, and the marker CloseRemindedAt on them (BR-CYCLE-07, HARVEST-01.2).</summary>
public class HarvestCorrectionsQaTestsAuditAndReminder
{
    private const string ReminderType = "CYCLE_CLOSE_REMINDER";

    private static Task<List<AuditEvent>> AuditRowsAsync(AuthApiFactory api, string action, Guid lotId) =>
        api.QueryAsync(db => db.AuditEvents.Where(e => e.Action == action && e.RecordId == lotId).ToListAsync());

    private static Task<DateTimeOffset?> MarkerAsync(AuthApiFactory api, Guid cycleId) =>
        api.QueryAsync(db => db.CultivationCycles.Where(c => c.Id == cycleId).Select(c => c.CloseRemindedAt).SingleAsync());

    private static Task<int> RemindersAsync(AuthApiFactory api, Guid cycleId, Guid account) =>
        api.QueryAsync(db => db.Notifications.CountAsync(n => n.Type == ReminderType && n.RelatedRecordId == cycleId && n.RecipientAccountId == account));

    // GBR-02: values before and after; only what changed. Other fields stay out, also in a save that changed the photos.
    [Fact]
    public async Task A_correction_writes_one_row_with_only_what_changed_the_trimmed_reason_the_actor_and_the_zone()
    {
        using var api = new AuthApiFactory();
        var raft = await api.SeededRaftAsync();
        var manager = await api.ManagerAsync(raft.ZoneId);
        var lot = await manager.Client.HarvestLotAsync(raft.RaftId, new HarvestFields { Lanes = ["1", "2"], Finished = ["2"], Photos = [Jpeg] });
        var keys = lot.PhotoKeysOfLot();

        await (await api.SendCorrectionAsync(manager.Client, lot.Id(), new HarvestFields { Lanes = ["1", "2"], Finished = ["2"], Cages = "9" }, "  Counted again  ", keys)).ReadAsync(HttpStatusCode.OK);

        var row = Assert.Single(await AuditRowsAsync(api, "HarvestLot.Corrected", lot.Id()));
        Assert.Equal(("Counted again", manager.Account.Id, raft.ZoneId, lot.Text("lotCode"), "HarvestLot"), (row.Reason, row.ActorAccountId, row.ZoneId, row.RecordCode, row.RecordType));
        using var before = JsonDocument.Parse(row.Before!);
        using var after = JsonDocument.Parse(row.After!);
        Assert.Equal(["CageCount", "EstimatedKg"], before.RootElement.EnumerateObject().Select(p => p.Name).Order());
        Assert.Equal(["CageCount", "EstimatedKg"], after.RootElement.EnumerateObject().Select(p => p.Name).Order());
        Assert.Equal((8, 400m, 9, 450m), (before.RootElement.GetProperty("CageCount").GetInt32(), before.RootElement.GetProperty("EstimatedKg").GetDecimal(), after.RootElement.GetProperty("CageCount").GetInt32(), after.RootElement.GetProperty("EstimatedKg").GetDecimal()));
    }

    // A correction changing nothing: the same lanes in another order, the same estimate at another scale, the same photos in another order. Nothing to write under GBR-02, and the client list says so.
    [Fact]
    public async Task A_save_of_the_same_values_in_another_spelling_writes_nothing()
    {
        using var api = new AuthApiFactory();
        var raft = await api.SeededRaftAsync();
        var owner = raft.Owner.Client;
        var lot = await owner.HarvestLotAsync(raft.RaftId, new HarvestFields { Lanes = ["1", "2", "3"], Finished = ["1", "3"], Photos = [Jpeg, Png] });
        var keys = lot.PhotoKeysOfLot();

        var fields = new HarvestFields { Lanes = ["3", "2", "1"], Finished = ["3", "1"], Cages = "08", EstimatedKg = "400.00" };
        var saved = await (await api.SendCorrectionAsync(owner, lot.Id(), fields, keep: [keys[1], keys[0], keys[1]])).ReadAsync(HttpStatusCode.OK);

        Assert.Empty(await AuditRowsAsync(api, "HarvestLot.Corrected", lot.Id()));
        Assert.Equal(keys, saved.GetProperty("lot").PhotoKeysOfLot());
        Assert.Equal(8, saved.GetProperty("lot").GetProperty("cageCount").GetDecimal());
    }

    // The reason: required for all (BR-HARVEST-05), counted after the trim; the longest fits on SQL Server (see the SQL Server tests).
    [Theory]
    [InlineData(null, false)]
    [InlineData("", false)]
    [InlineData("   ", false)]
    [InlineData("x", true)]
    public async Task A_reason_is_required_on_both_routes_also_when_nothing_changes(string? reason, bool accepted)
    {
        using var api = new AuthApiFactory();
        var raft = await api.SeededRaftAsync();
        var owner = raft.Owner.Client;
        var lot = await owner.HarvestLotAsync(raft.RaftId, HarvestFields.OfLanes(1));

        var correction = await api.SendCorrectionAsync(owner, lot.Id(), HarvestFields.OfLanes(1), reason);
        var removal = await api.SendRemovalAsync(owner, lot.Id(), reason);

        Assert.Equal(accepted ? HttpStatusCode.OK : HttpStatusCode.BadRequest, correction.StatusCode);
        Assert.Equal(accepted ? HttpStatusCode.NoContent : HttpStatusCode.BadRequest, removal.StatusCode);
        if (!accepted)
        {
            Assert.Equal("Reason", (await correction.ReadAsync(HttpStatusCode.BadRequest)).Text("field"));
            Assert.Equal("Reason", (await removal.ReadAsync(HttpStatusCode.BadRequest)).Text("field"));
        }
    }

    [Fact]
    public async Task A_removal_writes_one_row_with_the_whole_lot_before_nothing_after_and_the_reason()
    {
        using var api = new AuthApiFactory();
        var raft = await api.SeededRaftAsync();
        var lot = await raft.Owner.Client.HarvestLotAsync(raft.RaftId, new HarvestFields { Lanes = ["1", "2"], Finished = ["2"], Photos = [Jpeg] });

        await api.RemoveLotAsync(raft.Owner.Client, lot.Id());

        var row = Assert.Single(await AuditRowsAsync(api, "HarvestLot.Removed", lot.Id()));
        Assert.Null(row.After);
        using var before = JsonDocument.Parse(row.Before!);
        Assert.Equal(
            ["CageCount", "EstimatedKg", "FinishedLanes", "HarvestDate", "HarvestedAt", "KgPerCage", "LanePercents", "Lanes", "LotCode", "OverPlanReason", "Photos", "PurchaseRequestId"],
            before.RootElement.EnumerateObject().Select(p => p.Name).Order());
        Assert.Equal(lot.PhotoKeysOfLot()[0], before.RootElement.GetProperty("Photos")[0].GetString());
    }

    // BR-TRACE-05: the row names a Published lot it changed. Withdrawn and Not published are not Published.
    [Theory]
    [InlineData(true, false, true)]
    [InlineData(false, false, false)]
    [InlineData(true, true, false)]
    public async Task Only_a_published_lot_is_named_in_the_row_and_its_origin_state_does_not_move(bool published, bool withdrawn, bool named)
    {
        using var api = new AuthApiFactory();
        var raft = await api.SeededRaftAsync();
        var owner = raft.Owner.Client;
        var lot = await owner.HarvestLotAsync(raft.RaftId, HarvestFields.OfLanes(1));
        var at = new DateTimeOffset(2026, 10, 26, 9, 0, 0, TimeSpan.FromHours(7));
        await api.UpdateAsync<HarvestLot>(lot.Id(), l => (l.PublishedAt, l.WithdrawnAt) = (published ? at : null, withdrawn ? at.AddDays(1) : null));
        var state = (await owner.GetHarvestLotAsync(lot.Id())).Text("originState");

        var saved = (await api.CorrectLotAsync(owner, lot.Id(), new HarvestFields { Cages = "9" })).GetProperty("lot");

        Assert.Equal(state, saved.Text("originState"));
        var row = Assert.Single(await AuditRowsAsync(api, "HarvestLot.Corrected", lot.Id()));
        Assert.Equal(named, row.After!.Contains("AffectedPublishedLotCodes"));
    }

    // The client list, section 4: "A correction or removal that takes a lane out of Harvested arms it again: finishing that lane later sends a second reminder."
    // BR-CYCLE-07 reminds when the last seeded lane is marked. Here the removal takes both lanes out of Harvested while a lane seeded after the first reminder is still Growing.
    [Fact]
    public async Task L1_a_removal_that_takes_a_lane_out_of_harvested_arms_the_reminder_also_with_a_lane_seeded_after_it()
    {
        using var api = new AuthApiFactory();
        var raft = await api.SeededRaftAsync(1);
        var owner = raft.Owner.Client;
        var manager = await api.ManagerAsync(raft.ZoneId);
        var lot = await manager.Client.HarvestLotAsync(raft.RaftId, HarvestFields.OfLanes(1).FinishedLanes(1));
        Assert.Equal(1, await RemindersAsync(api, raft.CycleId, manager.Account.Id));
        await owner.RecordSeedingAsync(raft.CycleId, raft.Lot.Session([2]));

        await api.RemoveLotAsync(manager.Client, lot.Id());
        await manager.Client.HarvestLotAsync(raft.RaftId, HarvestFields.OfLanes(1, 2).FinishedLanes(1, 2));

        Assert.Equal(2, await RemindersAsync(api, raft.CycleId, manager.Account.Id));
    }

    [Fact]
    public async Task L1_a_correction_that_takes_a_lane_out_of_harvested_arms_the_reminder_also_with_a_lane_seeded_after_it()
    {
        using var api = new AuthApiFactory();
        var raft = await api.SeededRaftAsync(1);
        var owner = raft.Owner.Client;
        var manager = await api.ManagerAsync(raft.ZoneId);
        var lot = await manager.Client.HarvestLotAsync(raft.RaftId, HarvestFields.OfLanes(1).FinishedLanes(1));
        await owner.RecordSeedingAsync(raft.CycleId, raft.Lot.Session([2]));

        await api.CorrectLotAsync(manager.Client, lot.Id(), HarvestFields.OfLanes(1));
        await manager.Client.HarvestLotAsync(raft.RaftId, HarvestFields.OfLanes(2).FinishedLanes(2));
        await api.CorrectLotAsync(manager.Client, lot.Id(), HarvestFields.OfLanes(1).FinishedLanes(1));

        Assert.Equal(2, await RemindersAsync(api, raft.CycleId, manager.Account.Id));
    }

    // Nobody is told twice for one act: a repeat of the same PUT, or a correction of another field, with every lane Harvested.
    [Fact]
    public async Task Repeats_of_the_same_correction_tell_nobody_again_and_one_act_tells_each_manager_once()
    {
        using var api = new AuthApiFactory();
        var raft = await api.SeededRaftAsync(1);
        var (first, second) = (await api.ManagerAsync(raft.ZoneId), await api.ManagerAsync(raft.ZoneId));
        var lot = await first.Client.HarvestLotAsync(raft.RaftId, HarvestFields.OfLanes(1));

        var answer = await api.CorrectLotAsync(first.Client, lot.Id(), HarvestFields.OfLanes(1).FinishedLanes(1));
        Assert.True(answer.Flag("everySeededLaneHarvested"));
        for (var repeat = 0; repeat < 3; repeat++)
        {
            Assert.True((await api.CorrectLotAsync(first.Client, lot.Id(), HarvestFields.OfLanes(1).FinishedLanes(1))).Flag("everySeededLaneHarvested"));
        }

        await api.CorrectLotAsync(first.Client, lot.Id(), HarvestFields.OfLanes(1).FinishedLanes(1) with { Cages = "9" });

        Assert.Equal(1, await RemindersAsync(api, raft.CycleId, first.Account.Id));
        Assert.Equal(1, await RemindersAsync(api, raft.CycleId, second.Account.Id));
        var marker = await MarkerAsync(api, raft.CycleId);
        Assert.NotNull(marker);
        var pushes = api.Services.GetRequiredService<FakePushSender>().Inbox.List(second.Account.Id.ToString());
        Assert.Single(pushes, m => m.Subject == "Reminder to close a cultivation cycle");
    }

    // OMTS-216: an Owner is not told of their own act. With no manager in the zone the other Owners are; with only the acting Owner nobody is, and the answer alone says it.
    [Fact]
    public async Task An_owner_correcting_the_last_lane_with_no_manager_tells_the_other_owner_only_and_a_removal_arms_it_for_a_second_act()
    {
        using var api = new AuthApiFactory();
        var raft = await api.SeededRaftAsync(1);
        var owner = raft.Owner;
        var otherOwner = await api.OwnerAsync();
        var lot = await owner.Client.HarvestLotAsync(raft.RaftId, HarvestFields.OfLanes(1));

        var saved = await api.CorrectLotAsync(owner.Client, lot.Id(), HarvestFields.OfLanes(1).FinishedLanes(1));

        Assert.True(saved.Flag("everySeededLaneHarvested"));
        Assert.Equal(0, await RemindersAsync(api, raft.CycleId, owner.Account.Id));
        Assert.Equal(1, await RemindersAsync(api, raft.CycleId, otherOwner.Account.Id));

        await api.RemoveLotAsync(otherOwner.Client, lot.Id());
        Assert.Null(await MarkerAsync(api, raft.CycleId));
        await otherOwner.Client.HarvestLotAsync(raft.RaftId, HarvestFields.OfLanes(1).FinishedLanes(1));
        Assert.Equal(1, await RemindersAsync(api, raft.CycleId, owner.Account.Id));
        Assert.Equal(1, await RemindersAsync(api, raft.CycleId, otherOwner.Account.Id));
    }

    // HARVEST-01.1 step 4, and the removal: MSG-HARVEST-01 goes out once for the lot; a correction, a repeat and a removal add nothing, and the text sent stays.
    [Fact]
    public async Task The_notification_of_the_lot_goes_once_keeps_its_text_and_opening_it_after_a_correction_works()
    {
        using var api = new AuthApiFactory();
        var raft = await api.SeededRaftAsync();
        var owner = raft.Owner.Client;
        var manager = await api.ManagerAsync(raft.ZoneId);
        var lot = await manager.Client.HarvestLotAsync(raft.RaftId, HarvestFields.OfLanes(1));
        var notice = Assert.Single(await api.NoticesAsync("HARVEST_LOT_RECORDED", lot.Id()));

        await api.CorrectLotAsync(manager.Client, lot.Id(), new HarvestFields { Cages = "9" });
        await api.CorrectLotAsync(owner, lot.Id(), new HarvestFields { Cages = "10" });

        var after = Assert.Single(await api.NoticesAsync("HARVEST_LOT_RECORDED", lot.Id()));
        Assert.Equal(notice.Body, after.Body);
        Assert.Contains("8 cages", after.Body);
        Assert.Equal(HttpStatusCode.OK, (await owner.GetAsync($"/api/me/notifications/{notice.Id}/open")).StatusCode);

        // MSG-ACCOUNT-22 as Part V spells it, once the lot is gone; the row itself stays with its text (BR-ACCOUNT-12).
        await api.RemoveLotAsync(owner, lot.Id());
        var gone = await (await owner.GetAsync($"/api/me/notifications/{notice.Id}/open")).ReadAsync(HttpStatusCode.NotFound);
        Assert.Equal(("MSG-ACCOUNT-22", $"Harvest lot {lot.Text("lotCode")} no longer exists."), (gone.Text("code"), gone.Text("detail")));
        Assert.Equal(notice.Body, Assert.Single(await api.NoticesAsync("HARVEST_LOT_RECORDED", lot.Id())).Body);
    }
}
