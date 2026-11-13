using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using OMTS.Api.IntegrationTests.Cultivation;
using OMTS.Api.IntegrationTests.Farming;
using OMTS.Api.IntegrationTests.Infrastructure;
using OMTS.Domain.Constants;
using OMTS.Domain.Entities.Cultivation;
using static OMTS.Api.IntegrationTests.Harvest.HarvestLotCorrectionTestKit;

namespace OMTS.Api.IntegrationTests.Harvest;

/// <summary>HARVEST-01.1 step 3 and BR-HARVEST-05: a lot on an invoice stays, a removed lot lives only in the audit log, and its lanes are worked out from the other lots.</summary>
public class HarvestLotRemovalTests
{
    // Through the port: SALE-05 comes with 6a, and the double says the lot is on an invoice, also a cancelled one, which keeps its lines.
    [Fact]
    public async Task A_lot_on_an_invoice_cannot_be_removed_but_can_still_be_corrected_and_the_commands_say_so()
    {
        using var api = new SalesDoubleApiFactory();
        var raft = await api.SeededRaftAsync();
        var manager = await api.ManagerAsync(raft.ZoneId);
        var owner = raft.Owner.Client;
        var lot = await manager.Client.HarvestLotAsync(raft.RaftId, HarvestFields.OfLanes(1));
        api.Sales.OnInvoice.Add(lot.Id());

        foreach (var client in new[] { manager.Client, owner })
        {
            var refused = await (await api.SendRemovalAsync(client, lot.Id())).ReadAsync(HttpStatusCode.Conflict);
            Assert.Equal(("MSG-GEN-09", $"Harvest lot {lot.Text("lotCode")} is on a sales invoice and cannot be used for this action."), (refused.Text("code"), refused.Text("detail")));
            var read = await client.GetHarvestLotAsync(lot.Id());
            Assert.True(read.GetProperty("canCorrect").GetBoolean());
            Assert.False(read.GetProperty("canRemove").GetBoolean());
            var item = (await client.HarvestScreenAsync(raft.RaftId)).GetProperty("lots")[0];
            Assert.True(item.GetProperty("canCorrect").GetBoolean());
            Assert.False(item.GetProperty("canRemove").GetBoolean());
        }

        Assert.Equal(1, await api.LotCountAsync(raft.RaftId));
        Assert.Equal("BeingHarvested", (await owner.HarvestScreenAsync(raft.RaftId)).HarvestStateOf(1));
        Assert.Equal(0, await api.QueryAsync(db => db.AuditEvents.CountAsync(e => e.Action == "HarvestLot.Removed")));

        // BR-HARVEST-05 limits only the removal: all the harvest data can still be corrected.
        var saved = await api.CorrectLotAsync(manager.Client, lot.Id(), HarvestFields.OfLanes(1) with { Cages = "9" });
        Assert.Equal(9, saved.GetProperty("lot").GetProperty("cageCount").GetDecimal());

        // Off the invoice, it can go.
        api.Sales.OnInvoice.Clear();
        await api.RemoveLotAsync(manager.Client, lot.Id());
    }

    [Fact]
    public async Task A_refused_removal_on_an_invoice_is_refused_before_the_reason_and_the_version()
    {
        using var api = new SalesDoubleApiFactory();
        var raft = await api.SeededRaftAsync();
        var lot = await raft.Owner.Client.HarvestLotAsync(raft.RaftId);
        api.Sales.OnInvoice.Add(lot.Id());

        Assert.Equal("MSG-GEN-09", await (await raft.Owner.Client.PostRemoveLotAsync(lot.Id(), null, null)).ErrorCodeAsync());
    }

    // HARVEST-01.1 step 3, the example of the SRS: a lane in no other lot shows Growing again.
    [Fact]
    public async Task A_lane_in_no_other_lot_is_growing_again_and_a_lane_in_another_lot_stays_as_that_lot_says()
    {
        using var api = new AuthApiFactory();
        var raft = await api.SeededRaftAsync();
        var owner = raft.Owner.Client;
        var other = await owner.HarvestLotAsync(raft.RaftId, HarvestFields.OfLanes(2, 3).FinishedLanes(3));
        var lot = await owner.HarvestLotAsync(raft.RaftId, HarvestFields.OfLanes(1, 2).FinishedLanes(1));
        Assert.Equal("Harvested", (await owner.HarvestScreenAsync(raft.RaftId)).HarvestStateOf(1));

        await api.RemoveLotAsync(owner, lot.Id());

        var screen = await owner.HarvestScreenAsync(raft.RaftId);
        Assert.Equal("Growing", screen.HarvestStateOf(1));
        Assert.True(screen.CanHarvest(1));
        Assert.Equal("BeingHarvested", screen.HarvestStateOf(2));
        Assert.Equal("Harvested", screen.HarvestStateOf(3));
        Assert.Equal([other.Text("lotCode")], screen.LotCodes());
        Assert.Equal(HttpStatusCode.NotFound, (await owner.GetAsync($"/api/harvest-lots/{lot.Id()}")).StatusCode);
        Assert.Empty((await owner.GetFromJsonAsync<JsonElement>($"/api/rafts/{raft.RaftId}/lanes/1/progress")).GetProperty("harvestLots").EnumerateArray());
        Assert.Equal(0, await api.QueryAsync(db => db.HarvestLotLanes.CountAsync(l => l.LotId == lot.Id())));

        // The lane can be harvested and finished again.
        await owner.HarvestLotAsync(raft.RaftId, HarvestFields.OfLanes(1).FinishedLanes(1));
        Assert.Equal("Harvested", (await owner.HarvestScreenAsync(raft.RaftId)).HarvestStateOf(1));
    }

    // POST-4: the lot exists only in the audit log, with the reason; the Owner's notification stays and opening it says the record is gone (BR-ACCOUNT-12).
    [Fact]
    public async Task The_audit_log_keeps_the_lot_with_the_reason_and_its_notification_stays_and_answers_it_is_gone()
    {
        using var api = new AuthApiFactory();
        var raft = await api.SeededRaftAsync();
        var manager = await api.ManagerAsync(raft.ZoneId);
        var lot = await manager.Client.HarvestLotAsync(raft.RaftId, new HarvestFields { Lanes = ["1"], Finished = ["1"], Cages = "4", EstimatedKg = "180", Photos = [FarmingTestKit.Jpeg] });
        var notice = Assert.Single(await api.NoticesAsync("HARVEST_LOT_RECORDED", lot.Id()));

        Assert.Equal(HttpStatusCode.NoContent, (await api.SendRemovalAsync(manager.Client, lot.Id(), "  Wrong raft  ")).StatusCode);

        var row = await api.QueryAsync(db => db.AuditEvents.SingleAsync(e => e.Action == "HarvestLot.Removed"));
        Assert.Equal(("Wrong raft", lot.Id(), lot.Text("lotCode"), raft.ZoneId, manager.Account.Id), (row.Reason, row.RecordId!.Value, row.RecordCode, row.ZoneId, row.ActorAccountId));
        using var before = JsonDocument.Parse(row.Before!);
        Assert.Equal(lot.Text("lotCode"), before.RootElement.GetProperty("LotCode").GetString());
        Assert.Equal(180m, before.RootElement.GetProperty("EstimatedKg").GetDecimal());
        Assert.Equal(1, before.RootElement.GetProperty("FinishedLanes")[0].GetInt32());
        Assert.Single(await api.NoticesAsync("HARVEST_LOT_RECORDED", lot.Id()));

        var opened = await (await raft.Owner.Client.GetAsync($"/api/me/notifications/{notice.Id}/open")).ReadAsync(HttpStatusCode.NotFound);
        Assert.Equal("MSG-ACCOUNT-22", opened.Text("code"));
    }

    // CYCLE-02: a lane in a lot blocks its session; with the lot gone the session can be corrected and removed again, and with no record left the cycle can be cancelled.
    [Fact]
    public async Task Removing_the_lot_frees_the_seeding_session_and_the_cycle_can_be_cancelled_again()
    {
        using var api = new AuthApiFactory();
        var raft = await api.SeededRaftAsync(1);
        var owner = raft.Owner.Client;
        var lot = await owner.HarvestLotAsync(raft.RaftId, HarvestFields.OfLanes(1));
        Assert.False((await owner.GetSeedingAsync(raft.Session.Id())).GetProperty("canCorrect").GetBoolean());
        var cancelBlocked = await owner.PostAsJsonAsync($"/api/cultivation-cycles/{raft.CycleId}/cancel", new { rowVersion = "" });
        Assert.Equal("MSG-GEN-06", await cancelBlocked.ErrorCodeAsync());

        await api.RemoveLotAsync(owner, lot.Id());

        Assert.True((await owner.GetSeedingAsync(raft.Session.Id())).GetProperty("canCorrect").GetBoolean());
        Assert.Equal(HttpStatusCode.NoContent, (await owner.RemoveSeedingAsync(raft.Session.Id())).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await owner.PostAsJsonAsync($"/api/cultivation-cycles/{raft.CycleId}/cancel", new { rowVersion = "" })).StatusCode);
    }

    private static async Task<int> DaysOfFarmingAfterCloseAsync(CyclesQaClockApiFactory api, HttpClient owner, Guid cycleId)
    {
        api.Clock.At = InVietnam(10, 20);
        await api.UpdateAsync<CultivationCycle>(cycleId, c =>
        {
            c.StatusId = StatusIds.CultivationCycle.Closed;
            c.CloseDate = new DateOnly(2026, 10, 20);
        });
        return (await owner.GetFromJsonAsync<JsonElement>($"/api/cultivation-cycles/{cycleId}")).GetProperty("daysOfFarming").GetInt32();
    }

    // BR-CYCLE-06: the days run to the last lot that still stands.
    [Fact]
    public async Task The_days_of_farming_run_to_the_last_lot_left_after_a_removal()
    {
        using var api = new CyclesQaClockApiFactory();
        var (owner, _, raftId, cycleId) = await api.ClockedRaftAsync();
        api.Clock.At = InVietnam(10, 5);
        await owner.HarvestLotAsync(raftId, HarvestFields.OfLanes(1));
        api.Clock.At = InVietnam(10, 10);
        var late = await owner.HarvestLotAsync(raftId, HarvestFields.OfLanes(2));

        await api.RemoveLotAsync(owner, late.Id());

        Assert.Equal(5, await DaysOfFarmingAfterCloseAsync(api, owner, cycleId));
    }

    // BR-CYCLE-06: with no lot left, to the close date. A correction changes no date, so it does not move them.
    [Fact]
    public async Task The_days_of_farming_run_to_the_close_date_with_no_lot_left_and_a_correction_does_not_move_them()
    {
        using var api = new CyclesQaClockApiFactory();
        var (owner, _, raftId, cycleId) = await api.ClockedRaftAsync();
        api.Clock.At = InVietnam(10, 10);
        var lot = await owner.HarvestLotAsync(raftId, HarvestFields.OfLanes(1));
        api.Clock.At = InVietnam(10, 12);
        await api.CorrectLotAsync(owner, lot.Id(), HarvestFields.OfLanes(1, 2));
        Assert.Equal(new DateOnly(2026, 10, 10), (await owner.GetHarvestLotAsync(lot.Id())).Date("harvestDate"));

        await api.RemoveLotAsync(owner, lot.Id());

        Assert.Equal(20, await DaysOfFarmingAfterCloseAsync(api, owner, cycleId));
    }

    [Fact]
    public async Task A_lot_gone_answers_404_to_a_correction_and_a_second_removal()
    {
        using var api = new AuthApiFactory();
        var raft = await api.SeededRaftAsync();
        var owner = raft.Owner.Client;
        var lot = await owner.HarvestLotAsync(raft.RaftId);
        var version = await api.LotVersionAsync(lot.Id());

        Assert.Equal(HttpStatusCode.NoContent, (await owner.PostRemoveLotAsync(lot.Id(), "mistake", version)).StatusCode);

        Assert.Equal(HttpStatusCode.NotFound, (await owner.PostRemoveLotAsync(lot.Id(), "mistake", version)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await owner.PutHarvestLotAsync(lot.Id(), new HarvestCorrection(new HarvestFields(), RowVersion: version))).StatusCode);
        Assert.Empty(await api.QueryAsync(db => db.HarvestLots.Where(l => l.Id == lot.Id()).ToListAsync()));
    }
}
