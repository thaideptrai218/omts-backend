using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using OMTS.Api.IntegrationTests.Cultivation;
using OMTS.Api.IntegrationTests.Farming;
using OMTS.Api.IntegrationTests.Infrastructure;
using OMTS.Application.Features.Harvest;
using OMTS.Domain.Constants;
using OMTS.Domain.Entities.Cultivation;
using OMTS.Domain.Entities.Farming;
using OMTS.Domain.Entities.Harvest;
using OMTS.Domain.Entities.Identity;
using OMTS.Domain.Enums;
using static OMTS.Api.IntegrationTests.Farming.FarmingTestKit;

namespace OMTS.Api.IntegrationTests.Harvest;

/// <summary>QA of PR H1b: the own-record rule per role and pair, the state rules, the lanes on every view and the estimate. Finding tests are named after the finding of the report.</summary>
public class HarvestCorrectionsQaTests(AuthApiFactory factory) : IClassFixture<AuthApiFactory>
{
    private static async Task AssertForbiddenAsync(HttpResponseMessage answer)
    {
        Assert.Equal(HttpStatusCode.Forbidden, answer.StatusCode);
        Assert.Equal("MSG-GEN-08", await answer.ErrorCodeAsync());
    }

    private static async Task AssertFieldRefusedAsync(HttpResponseMessage answer, string field)
    {
        var body = await answer.ReadAsync(HttpStatusCode.BadRequest);
        Assert.Equal(("MSG-GEN-01", field), (body.Text("code"), body.Text("field")));
    }

    private async Task AssertRefusedEverywhereAsync(AuthApiFactory api, HttpClient client, Guid lotId)
    {
        await AssertForbiddenAsync(await api.SendCorrectionAsync(client, lotId, new HarvestFields { Cages = "9" }));
        await AssertForbiddenAsync(await api.SendRemovalAsync(client, lotId));
    }

    private Task RevokeAsync(AuthApiFactory api, Guid grantId) =>
        api.UpdateAsync<AccessGrant>(grantId, g => { g.StatusId = StatusIds.AccessGrant.Revoked; g.RevokedAt = DateTimeOffset.UtcNow; });

    private static Task<decimal> CagesAsync(HttpClient client, Guid lotId) => client.GetHarvestLotAsync(lotId).ContinueWith(t => t.Result.GetProperty("cageCount").GetDecimal());

    private Task<int> AuditRowsAsync(string action, Guid lotId) => factory.QueryAsync(db => db.AuditEvents.CountAsync(e => e.Action == action && e.RecordId == lotId));

    // BR-HARVEST-05: "that they recorded". A recorder who moved away has no right left in the zone; nor does the manager who took the zone over; the Owner still does, and the lot stays theirs.
    [Fact]
    public async Task A_lot_of_a_manager_since_moved_away_is_the_owners_alone_and_the_recorder_gets_it_back_with_a_new_pair()
    {
        var raft = await factory.SeededRaftAsync();
        var owner = raft.Owner.Client;
        var recorder = await factory.ManagerAsync(raft.ZoneId);
        var successor = await factory.ManagerAsync(raft.ZoneId);
        var elsewhere = await factory.ZoneEntityAsync((await owner.CreateZoneAsync()).Id());
        var lot = await recorder.Client.HarvestLotAsync(raft.RaftId, HarvestFields.OfLanes(1));

        await RevokeAsync(factory, recorder.Grant!.Id);
        await factory.GrantAsync(recorder.Account, StaffRole.ZoneManager, elsewhere);

        await AssertRefusedEverywhereAsync(factory, recorder.Client, lot.Id());
        await AssertRefusedEverywhereAsync(factory, successor.Client, lot.Id());
        var seen = await successor.Client.GetHarvestLotAsync(lot.Id());
        Assert.Equal((false, false), (seen.Flag("canCorrect"), seen.Flag("canRemove")));
        var item = (await successor.Client.HarvestScreenAsync(raft.RaftId)).GetProperty("lots")[0];
        Assert.Equal((false, false), (item.Flag("canCorrect"), item.Flag("canRemove")));

        var byOwner = (await factory.CorrectLotAsync(owner, lot.Id(), HarvestFields.OfLanes(1) with { Cages = "9" })).GetProperty("lot");
        Assert.Equal(recorder.Account.Id, byOwner.GetProperty("recordedByAccountId").GetGuid());

        await factory.GrantAsync(recorder.Account, StaffRole.ZoneManager, await factory.ZoneEntityAsync(raft.ZoneId));
        var back = await recorder.Client.GetHarvestLotAsync(lot.Id());
        Assert.Equal((true, true), (back.Flag("canCorrect"), back.Flag("canRemove")));
        await factory.CorrectLotAsync(recorder.Client, lot.Id(), HarvestFields.OfLanes(1) with { Cages = "7" });
    }

    [Fact]
    public async Task A_recorder_with_only_accountant_pairs_left_here_is_refused_and_one_with_a_manager_pair_on_all_zones_is_not()
    {
        var raft = await factory.SeededRaftAsync();
        var here = await factory.ZoneEntityAsync(raft.ZoneId);
        var recorder = await factory.ManagerAsync(raft.ZoneId);
        var lot = await recorder.Client.HarvestLotAsync(raft.RaftId, HarvestFields.OfLanes(1));

        await factory.GrantAsync(recorder.Account, StaffRole.Accountant, null);
        await factory.CorrectLotAsync(recorder.Client, lot.Id(), HarvestFields.OfLanes(1) with { Cages = "6" });

        await RevokeAsync(factory, recorder.Grant!.Id);
        await factory.GrantAsync(recorder.Account, StaffRole.Accountant, here);
        await AssertRefusedEverywhereAsync(factory, recorder.Client, lot.Id());

        await factory.GrantAsync(recorder.Account, StaffRole.ZoneManager, null);
        await factory.CorrectLotAsync(recorder.Client, lot.Id(), HarvestFields.OfLanes(1) with { Cages = "5" });
        Assert.Equal(5, await CagesAsync(recorder.Client, lot.Id()));
    }

    [Fact]
    public async Task A_lot_the_owner_or_an_all_zones_manager_recorded_is_closed_to_the_managers_who_did_not()
    {
        var raft = await factory.SeededRaftAsync();
        var owner = raft.Owner.Client;
        var manager = await factory.ManagerAsync(raft.ZoneId);
        var allZones = await factory.StaffAsync(null);
        var ofOwner = await owner.HarvestLotAsync(raft.RaftId, HarvestFields.OfLanes(1));
        var ofAllZones = await allZones.Client.HarvestLotAsync(raft.RaftId, HarvestFields.OfLanes(2));
        var secondOwner = await factory.OwnerAsync();

        await AssertRefusedEverywhereAsync(factory, manager.Client, ofOwner.Id());
        await AssertRefusedEverywhereAsync(factory, allZones.Client, ofOwner.Id());
        await AssertRefusedEverywhereAsync(factory, manager.Client, ofAllZones.Id());

        await factory.CorrectLotAsync(allZones.Client, ofAllZones.Id(), HarvestFields.OfLanes(2) with { Cages = "9" });
        await factory.CorrectLotAsync(secondOwner.Client, ofOwner.Id(), HarvestFields.OfLanes(1) with { Cages = "9" });
        await factory.RemoveLotAsync(secondOwner.Client, ofAllZones.Id());
    }

    // GFR-02: access comes first, whatever else the request holds: bad fields, a part of another name, a stale version, a lot on an invoice.
    [Fact]
    public async Task A_caller_without_rights_learns_nothing_from_what_it_sent()
    {
        using var api = new SalesDoubleApiFactory();
        var raft = await api.SeededRaftAsync();
        var lot = await raft.Owner.Client.HarvestLotAsync(raft.RaftId, HarvestFields.OfLanes(1));
        api.Sales.OnInvoice.Add(lot.Id());
        var zone = await api.ZoneEntityAsync(raft.ZoneId);
        var callers = new[]
        {
            (await api.ManagerAsync(raft.ZoneId)).Client, (await api.StaffAsync(zone, StaffRole.Accountant)).Client, (await api.StaffAsync(null, StaffRole.Accountant)).Client, await api.TraderAsync(),
        };
        foreach (var caller in callers)
        {
            await AssertForbiddenAsync(await caller.PutHarvestLotAsync(lot.Id(), new HarvestCorrection(new HarvestFields { Lanes = ["99"], Cages = "0", EstimatedKg = "abc", PhotoField = "picture", Photos = [Pdf] }, null, null)));
            await AssertForbiddenAsync(await caller.PostRemoveLotAsync(lot.Id(), null, null));
        }

        await api.CloseCycleAsync(raft.CycleId);
        await AssertForbiddenAsync(await callers[0].PostRemoveLotAsync(lot.Id(), "x", AcsTestKit.StaleRowVersion));

        var anonymous = api.CreateClient();
        anonymous.DefaultRequestHeaders.Authorization = null;
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.PutHarvestLotAsync(lot.Id(), new HarvestCorrection(new HarvestFields()))).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.PostRemoveLotAsync(lot.Id(), "x", "x")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync("/api/harvest-lots/for-sale/count")).StatusCode);
    }

    // GFR-02 asks again at the save: the correction looks at the rows after its uploads, the access among them.
    [Fact]
    public async Task A_pair_revoked_during_the_upload_of_a_correction_refuses_it_and_removes_the_photo()
    {
        using var api = new CycleLogsQaFaultFactory();
        var raft = await api.SeededRaftAsync();
        var manager = await api.ManagerAsync(raft.ZoneId);
        var lot = await manager.Client.HarvestLotAsync(raft.RaftId, HarvestFields.OfLanes(1));
        var version = await api.LotVersionAsync(lot.Id());
        api.Hold.Arm();

        var pending = manager.Client.PutHarvestLotAsync(lot.Id(), new HarvestCorrection(new HarvestFields { Cages = "9", Photos = [Jpeg] }, "Wrong count", version));
        await api.Hold.Reached;
        await RevokeAsync(api, manager.Grant!.Id);
        api.Hold.Release();

        await AssertForbiddenAsync(await pending);
        await api.AssertNoOrphanLotPhotosAsync(FileCategories());
        Assert.Equal(8, await CagesAsync(raft.Owner.Client, lot.Id()));
    }

    private static string FileCategories() => OMTS.Application.Features.Files.FileCategories.HarvestLotPhotos;

    // BR-CYCLE-04: Closed is about the cycle of the lot; a new Open cycle of the raft changes nothing for the lots of the old one.
    [Fact]
    public async Task A_lot_of_a_closed_cycle_stays_locked_when_the_raft_has_a_new_open_cycle()
    {
        var raft = await factory.SeededRaftAsync();
        var owner = raft.Owner.Client;
        var manager = await factory.ManagerAsync(raft.ZoneId);
        var lot = await manager.Client.HarvestLotAsync(raft.RaftId, HarvestFields.OfLanes(1));
        await factory.UpdateAsync<CultivationCycle>(raft.CycleId, c =>
        {
            c.StatusId = StatusIds.CultivationCycle.Closed;
            c.CloseDate = Today.AddDays(-2);
        });
        await owner.StartCycleAsync(raft.RaftId, new CycleBody(Today));

        foreach (var client in new[] { owner, manager.Client })
        {
            Assert.Equal("MSG-GEN-09", await (await factory.SendCorrectionAsync(client, lot.Id(), HarvestFields.OfLanes(1) with { Cages = "9" })).ErrorCodeAsync());
            Assert.Equal("MSG-GEN-09", await (await factory.SendRemovalAsync(client, lot.Id())).ErrorCodeAsync());
            var read = await client.GetHarvestLotAsync(lot.Id());
            Assert.Equal((false, false), (read.Flag("canCorrect"), read.Flag("canRemove")));
        }
    }

    // BR-HARVEST-05 limits by the state of the cycle, and the removal by the invoice; nothing in it looks at the status of the lot. So a Closed or Reopened lot is corrected and removed while the cycle is Open.
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task A_closed_or_reopened_lot_of_an_open_cycle_is_corrected_and_removed_and_its_status_stays(bool reopened)
    {
        var raft = await factory.SeededRaftAsync();
        var owner = raft.Owner.Client;
        var manager = await factory.ManagerAsync(raft.ZoneId);
        var lot = await manager.Client.HarvestLotAsync(raft.RaftId, HarvestFields.OfLanes(1));
        await factory.UpdateAsync<HarvestLot>(lot.Id(), l =>
        {
            l.StatusId = reopened ? StatusIds.HarvestLot.Reopened : StatusIds.HarvestLot.Closed;
            l.CloseDate = reopened ? null : Today;
            l.LeftoverKg = reopened ? null : 100m;
            l.CloseReason = reopened ? null : HarvestLotCloseReason.Unsold;
        });
        var read = await manager.Client.GetHarvestLotAsync(lot.Id());
        Assert.Equal((true, true), (read.Flag("canCorrect"), read.Flag("canRemove")));

        var saved = (await factory.CorrectLotAsync(manager.Client, lot.Id(), HarvestFields.OfLanes(1, 2).FinishedLanes(1))).GetProperty("lot");

        Assert.Equal(reopened ? StatusIds.HarvestLot.Reopened : StatusIds.HarvestLot.Closed, saved.GetProperty("statusId").GetInt32());
        Assert.Equal(reopened ? 0m : 100m, saved.GetProperty("figures").Kg("lostKg"));
        await factory.RemoveLotAsync(manager.Client, lot.Id());
        Assert.Equal("Growing", (await owner.HarvestScreenAsync(raft.RaftId)).HarvestStateOf(1));
    }

    // Through the port, as the invoices come with 6a: a lot on an invoice, also a Closed one, is corrected and never removed, and its figures follow the correction.
    [Fact]
    public async Task A_lot_on_an_invoice_keeps_its_figures_when_corrected_and_stays_when_the_removal_is_refused()
    {
        using var api = new SalesDoubleApiFactory();
        var raft = await api.SeededRaftAsync();
        var owner = raft.Owner.Client;
        var lot = await owner.HarvestLotAsync(raft.RaftId, HarvestFields.OfLanes(1));
        api.Sales.OnInvoice.Add(lot.Id());
        api.Sales.Figures[lot.Id()] = new LotSalesFigures(350m, 0m, 0m, 0m);

        var corrected = (await api.CorrectLotAsync(owner, lot.Id(), HarvestFields.OfLanes(1) with { Cages = "6" })).GetProperty("lot");

        Assert.Equal(300m, corrected.Kg("estimatedKg"));
        Assert.Equal((350m, 0m, -50m), (corrected.GetProperty("figures").Kg("invoicedKg"), corrected.GetProperty("figures").Kg("remainingKg"), corrected.GetProperty("figures").Kg("differenceKg")));
        var refused = await api.SendRemovalAsync(owner, lot.Id());
        Assert.Equal(HttpStatusCode.Conflict, refused.StatusCode);
        Assert.Equal("MSG-GEN-09", await refused.ErrorCodeAsync());
        Assert.Equal(1, await api.LotCountAsync(raft.RaftId));

        // Sold whole and Closed: still corrected while the cycle is Open, never removed, and the status stays.
        await api.UpdateAsync<HarvestLot>(lot.Id(), l =>
        {
            l.StatusId = StatusIds.HarvestLot.Closed;
            l.CloseDate = Today;
            l.LeftoverKg = 0m;
            l.CloseReason = HarvestLotCloseReason.SoldWholeLot;
        });
        var closed = (await api.CorrectLotAsync(owner, lot.Id(), HarvestFields.OfLanes(1, 2))).GetProperty("lot");
        Assert.Equal((StatusIds.HarvestLot.Closed, true, false), (closed.GetProperty("statusId").GetInt32(), closed.Flag("canCorrect"), closed.Flag("canRemove")));
        Assert.Equal("MSG-GEN-09", await (await api.SendRemovalAsync(owner, lot.Id())).ErrorCodeAsync());
    }

    // GBR-01: a discontinued raft or zone is read-only, also for the Owner; the screen says so per lot, as the routes answer.
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task A_discontinued_raft_or_zone_turns_the_commands_off_on_the_lot_and_refuses_for_recorder_and_owner(bool zoneToo)
    {
        var raft = await factory.SeededRaftAsync();
        var owner = raft.Owner.Client;
        var manager = await factory.ManagerAsync(raft.ZoneId);
        var lot = await manager.Client.HarvestLotAsync(raft.RaftId, HarvestFields.OfLanes(1));
        if (zoneToo)
        {
            await factory.UpdateAsync<FarmingZone>(raft.ZoneId, z => z.StatusId = StatusIds.Usage.Discontinued);
        }
        else
        {
            await factory.DiscontinueRaftAsync(raft.RaftId);
        }

        foreach (var client in new[] { owner, manager.Client })
        {
            Assert.Equal("MSG-GEN-09", await (await factory.SendCorrectionAsync(client, lot.Id(), HarvestFields.OfLanes(1) with { Cages = "9" })).ErrorCodeAsync());
            Assert.Equal("MSG-GEN-09", await (await factory.SendRemovalAsync(client, lot.Id())).ErrorCodeAsync());
            var read = await client.GetHarvestLotAsync(lot.Id());
            Assert.Equal((false, false), (read.Flag("canCorrect"), read.Flag("canRemove")));
            var item = (await client.HarvestScreenAsync(raft.RaftId)).GetProperty("lots")[0];
            Assert.Equal((false, false), (item.Flag("canCorrect"), item.Flag("canRemove")));
        }
    }

    // BR-FARM-03 on every view that shows a lane: the harvest screen, the seeding screen, the raft's progress, the lane's own view.
    [Fact]
    public async Task Every_view_shows_the_lanes_as_worked_out_after_a_correction_and_a_removal()
    {
        var raft = await factory.SeededRaftAsync();
        var owner = raft.Owner.Client;
        var lot = await owner.HarvestLotAsync(raft.RaftId, HarvestFields.OfLanes(1, 2).FinishedLanes(1));
        await AssertStatesAsync(raft, ("Harvested", 1), ("BeingHarvested", 2), ("Growing", 3));

        await factory.CorrectLotAsync(owner, lot.Id(), HarvestFields.OfLanes(2, 3).FinishedLanes(3));

        await AssertStatesAsync(raft, ("Growing", 1), ("BeingHarvested", 2), ("Harvested", 3));
        var laneOne = await owner.GetFromJsonAsync<JsonElement>($"/api/rafts/{raft.RaftId}/lanes/1/progress");
        Assert.Empty(laneOne.GetProperty("harvestLots").EnumerateArray());
        var laneThree = await owner.GetFromJsonAsync<JsonElement>($"/api/rafts/{raft.RaftId}/lanes/3/progress");
        Assert.True(laneThree.GetProperty("harvestLots")[0].Flag("isFinished"));

        await factory.RemoveLotAsync(owner, lot.Id());

        await AssertStatesAsync(raft, ("Growing", 1), ("Growing", 2), ("Growing", 3));
    }

    private async Task AssertStatesAsync(SeededRaft raft, params (string State, int Lane)[] lanes)
    {
        var owner = raft.Owner.Client;
        var screen = await owner.HarvestScreenAsync(raft.RaftId);
        var seeding = await owner.SeedingListAsync(raft.CycleId);
        foreach (var (state, lane) in lanes)
        {
            Assert.Equal(state, screen.HarvestStateOf(lane));
            Assert.Equal(state, seeding.StateOf(lane));
            var view = await owner.GetFromJsonAsync<JsonElement>($"/api/rafts/{raft.RaftId}/lanes/{lane}/progress");
            Assert.Equal(state, view.Text("state"));
        }
    }

    // BR-FARM-03: with the lot gone and the session removed the lane has no seed: Not seeded, and a lot can no longer take it.
    [Fact]
    public async Task A_lane_goes_back_to_not_seeded_when_its_lot_and_its_session_are_gone_and_no_lot_can_take_it()
    {
        var raft = await factory.SeededRaftAsync(1, 2);
        var owner = raft.Owner.Client;
        var other = await owner.SeedingRecordAsync(raft.CycleId, raft.Lot.Session([3]));
        var keeper = await owner.HarvestLotAsync(raft.RaftId, HarvestFields.OfLanes(1));
        var lot = await owner.HarvestLotAsync(raft.RaftId, HarvestFields.OfLanes(3));
        Assert.Equal(HttpStatusCode.Conflict, (await owner.RemoveSeedingAsync(other.Id())).StatusCode);

        await factory.RemoveLotAsync(owner, lot.Id());
        Assert.Equal(HttpStatusCode.NoContent, (await owner.RemoveSeedingAsync(other.Id())).StatusCode);

        await AssertStatesAsync(raft, ("BeingHarvested", 1), ("Growing", 2), ("NotSeeded", 3));
        var refused = await (await factory.SendCorrectionAsync(owner, keeper.Id(), HarvestFields.OfLanes(1, 3))).ReadAsync(HttpStatusCode.BadRequest);
        Assert.Equal(("Lanes", "Lanes 3 cannot be harvested now."), (refused.Text("field"), refused.Text("hint")));
        await AssertFieldRefusedAsync(await owner.PostHarvestAsync(raft.RaftId, HarvestFields.OfLanes(3)), "Lanes");
    }

    // BR-CYCLE-02: a session is free while no lane of it is in a lot. A correction moves that line both ways, and a session with a lane still in a lot stays locked.
    [Fact]
    public async Task A_correction_locks_the_session_of_a_lane_it_adds_and_frees_the_session_of_a_lane_it_takes_out()
    {
        var raft = await factory.SeededRaftAsync(1, 2);
        var owner = raft.Owner.Client;
        var other = await owner.SeedingRecordAsync(raft.CycleId, raft.Lot.Session([3]));
        var lot = await owner.HarvestLotAsync(raft.RaftId, HarvestFields.OfLanes(1));
        Assert.False((await owner.GetSeedingAsync(raft.Session.Id())).Flag("canCorrect"));
        Assert.True((await owner.GetSeedingAsync(other.Id())).Flag("canCorrect"));

        await factory.CorrectLotAsync(owner, lot.Id(), HarvestFields.OfLanes(3));

        Assert.True((await owner.GetSeedingAsync(raft.Session.Id())).Flag("canCorrect"));
        Assert.False((await owner.GetSeedingAsync(other.Id())).Flag("canCorrect"));

        await factory.CorrectLotAsync(owner, lot.Id(), HarvestFields.OfLanes(2, 3));

        Assert.False((await owner.GetSeedingAsync(raft.Session.Id())).Flag("canCorrect"));
        Assert.Equal(HttpStatusCode.Conflict, (await owner.RemoveSeedingAsync(raft.Session.Id())).StatusCode);
    }

    // BR-CYCLE-02: a session correction may not add a lane in a lot: taken out of the lot by a correction it can, and the lane is Growing with the new session.
    [Fact]
    public async Task A_session_correction_cannot_take_a_lane_in_a_lot_and_can_once_a_correction_took_it_out()
    {
        var raft = await factory.SeededRaftAsync(1);
        var owner = raft.Owner.Client;
        var other = await owner.SeedingRecordAsync(raft.CycleId, raft.Lot.Session([2]));
        var lot = await owner.HarvestLotAsync(raft.RaftId, HarvestFields.OfLanes(2));
        var session = await owner.GetSeedingAsync(raft.Session.Id());
        Assert.Equal(HttpStatusCode.BadRequest, (await owner.PutSeedingAsync(session.Id(), session.AsCorrection() with { LaneNumbers = [1, 2] })).StatusCode);

        await factory.CorrectLotAsync(owner, lot.Id(), HarvestFields.OfLanes(1));
        Assert.Equal(HttpStatusCode.Conflict, (await owner.PutSeedingAsync(session.Id(), session.AsCorrection() with { LaneNumbers = [1, 2] })).StatusCode);

        var freed = await owner.GetSeedingAsync(other.Id());
        (await owner.PutSeedingAsync(freed.Id(), freed.AsCorrection() with { LaneNumbers = [2, 3] })).EnsureSuccessStatusCode();
        Assert.Equal("Growing", (await owner.HarvestScreenAsync(raft.RaftId)).HarvestStateOf(3));
    }

    // BR-CYCLE-02: a Growing lane can be hung again before the mark complete, a lane in a lot cannot. Taken out of its lot, it can again.
    [Fact]
    public async Task A_lane_taken_out_of_its_lot_can_be_hung_again_and_while_in_a_lot_it_cannot()
    {
        var raft = await factory.SeededRaftAsync(1, 2);
        var owner = raft.Owner.Client;
        // Lane 1 holds half, so a second session can hang the rest once it is out of its lot (a lane never holds more than 100 %).
        await owner.HalveLaneAsync(raft.Session, 1);
        var lot = await owner.HarvestLotAsync(raft.RaftId, HarvestFields.OfLanes(1, 2));
        Assert.Equal(HttpStatusCode.BadRequest, (await owner.PostSeedingAsync(raft.CycleId, raft.Lot.Session([1], Today).Percents(50m, 1))).StatusCode);

        await factory.CorrectLotAsync(owner, lot.Id(), HarvestFields.OfLanes(2));

        Assert.Equal(HttpStatusCode.Created, (await owner.PostSeedingAsync(raft.CycleId, raft.Lot.Session([1], Today).Percents(50m, 1))).StatusCode);
    }

    // SCR-HARVEST-01: Record harvest is offered only while a lane is Growing or Being harvested.
    [Fact]
    public async Task Record_harvest_comes_back_when_a_correction_or_a_removal_takes_a_lane_out_of_harvested()
    {
        var raft = await factory.SeededRaftAsync(1, 2);
        var owner = raft.Owner.Client;
        var lot = await owner.HarvestLotAsync(raft.RaftId, HarvestFields.OfLanes(1, 2).FinishedLanes(1, 2));
        Assert.False((await owner.HarvestScreenAsync(raft.RaftId)).Flag("canRecordHarvest"));

        await factory.CorrectLotAsync(owner, lot.Id(), HarvestFields.OfLanes(1, 2).FinishedLanes(1));
        var screen = await owner.HarvestScreenAsync(raft.RaftId);
        Assert.True(screen.Flag("canRecordHarvest"));
        Assert.Equal((false, true), (screen.CanHarvest(1), screen.CanHarvest(2)));

        await factory.CorrectLotAsync(owner, lot.Id(), HarvestFields.OfLanes(1, 2).FinishedLanes(1, 2));
        Assert.False((await owner.HarvestScreenAsync(raft.RaftId)).Flag("canRecordHarvest"));

        await factory.RemoveLotAsync(owner, lot.Id());
        Assert.True((await owner.HarvestScreenAsync(raft.RaftId)).Flag("canRecordHarvest"));
    }

    // BR-HARVEST-01 walked through both ways: two lots on one lane, the finished mark moved between them.
    [Fact]
    public async Task The_finished_mark_of_a_lane_in_two_lots_moves_only_to_the_later_lot()
    {
        var raft = await factory.SeededRaftAsync();
        var owner = raft.Owner.Client;
        var earlier = await owner.HarvestLotAsync(raft.RaftId, HarvestFields.OfLanes(1));
        var later = await owner.HarvestLotAsync(raft.RaftId, HarvestFields.OfLanes(1).FinishedLanes(1));

        // A lane that a later lot holds and finished stays allowed, and other fields can change.
        await factory.CorrectLotAsync(owner, earlier.Id(), HarvestFields.OfLanes(1) with { Cages = "9" });
        var hint = await (await factory.SendCorrectionAsync(owner, earlier.Id(), HarvestFields.OfLanes(1).FinishedLanes(1))).ReadAsync(HttpStatusCode.BadRequest);
        Assert.Equal(("Finished lanes", "Lanes 1 are in a later harvest lot and cannot be marked finished in this one."), (hint.Text("field"), hint.Text("hint")));

        // Out of the later lot, the earlier one may finish it; then the later one cannot take it back.
        await factory.CorrectLotAsync(owner, later.Id(), HarvestFields.OfLanes(2));
        Assert.Equal("BeingHarvested", (await owner.HarvestScreenAsync(raft.RaftId)).HarvestStateOf(1));
        await factory.CorrectLotAsync(owner, earlier.Id(), HarvestFields.OfLanes(1).FinishedLanes(1));
        var taken = await (await factory.SendCorrectionAsync(owner, later.Id(), HarvestFields.OfLanes(1, 2))).ReadAsync(HttpStatusCode.BadRequest);
        Assert.Equal(("Lanes", "Lanes 1 cannot be harvested now."), (taken.Text("field"), taken.Text("hint")));

        // Un-finished, the lane can be taken by the later lot; the earlier one then cannot finish it again.
        await factory.CorrectLotAsync(owner, earlier.Id(), HarvestFields.OfLanes(1));
        await factory.CorrectLotAsync(owner, later.Id(), HarvestFields.OfLanes(1, 2).FinishedLanes(1));
        Assert.Equal("Harvested", (await owner.HarvestScreenAsync(raft.RaftId)).HarvestStateOf(1));
    }

    [Theory]
    [InlineData("Lanes", new[] { "1", "1" }, new string[0])]
    [InlineData("Lanes", new[] { "01", "1" }, new string[0])]
    [InlineData("Lanes", new[] { "99" }, new string[0])]
    [InlineData("Lanes", new[] { "0" }, new string[0])]
    [InlineData("Lanes", new[] { "-1" }, new string[0])]
    [InlineData("Lanes", new[] { "abc" }, new string[0])]
    [InlineData("Lanes", new[] { "1,2" }, new string[0])]
    [InlineData("Lanes", new[] { "1.0" }, new string[0])]
    [InlineData("Lanes", new[] { "" }, new string[0])]
    [InlineData("Lanes", new string[0], new string[0])]
    [InlineData("Finished lanes", new[] { "1" }, new[] { "2" })]
    [InlineData("Finished lanes", new[] { "1" }, new[] { "1", "1" })]
    [InlineData("Finished lanes", new[] { "1" }, new[] { "x" })]
    public async Task Lanes_refused_on_a_correction_change_nothing(string field, string[] lanes, string[] finished)
    {
        var raft = await factory.SeededRaftAsync();
        var owner = raft.Owner.Client;
        var lot = await owner.HarvestLotAsync(raft.RaftId, HarvestFields.OfLanes(1));

        await AssertFieldRefusedAsync(await factory.SendCorrectionAsync(owner, lot.Id(), new HarvestFields { Lanes = lanes, Finished = finished }), field);

        var read = await owner.GetHarvestLotAsync(lot.Id());
        Assert.Equal([1], read.Numbers("laneNumbers"));
        Assert.Empty(read.Numbers("finishedLaneNumbers"));
        Assert.Equal(0, await AuditRowsAsync(HarvestLotAudit.Corrected, lot.Id()));
    }

    // BR-HARVEST-02: the copy of the lot, not the setting. Cages unchanged: the estimate stays, also by hand. Cages changed with none sent: worked out again. Sent: as sent.
    [Fact]
    public async Task The_estimate_follows_the_cages_with_the_kg_per_cage_of_the_lot_and_no_setting_reaches_it()
    {
        using var api = new AuthApiFactory();
        var raft = await api.SeededRaftAsync();
        var owner = raft.Owner.Client;
        var lot = await owner.HarvestLotAsync(raft.RaftId, new HarvestFields { Lanes = ["1"], Cages = "8", EstimatedKg = "450" });
        Assert.Equal((50m, 450m), (lot.Kg("kgPerCage"), lot.Kg("estimatedKg")));
        await owner.SetHarvestSettingsAsync(60m, 3);

        async Task<(decimal PerCage, decimal Estimate)> SendAsync(HarvestFields fields)
        {
            var saved = (await api.CorrectLotAsync(owner, lot.Id(), fields)).GetProperty("lot");
            return (saved.Kg("kgPerCage"), saved.Kg("estimatedKg"));
        }

        Assert.Equal((50m, 450m), await SendAsync(HarvestFields.OfLanes(1, 2)));
        Assert.Equal((50m, 500m), await SendAsync(new HarvestFields { Lanes = ["1", "2"], Cages = "10" }));
        Assert.Equal((50m, 512.5m), await SendAsync(new HarvestFields { Lanes = ["1", "2"], Cages = "10", EstimatedKg = "512.5" }));
        Assert.Equal((50m, 512.5m), await SendAsync(new HarvestFields { Lanes = ["1"], Cages = "10" }));
        Assert.Equal((50m, 600m), await SendAsync(new HarvestFields { Lanes = ["1"], Cages = "12", EstimatedKg = "600" }));
        Assert.Equal((50m, 400m), await SendAsync(new HarvestFields { Lanes = ["1"], Cages = "8" }));
        var read = await owner.GetHarvestLotAsync(lot.Id());
        Assert.Equal((50m, 400m), (read.Kg("kgPerCage"), read.Kg("estimatedKg")));
    }

    [Theory]
    [InlineData("Estimated kg", "8", "400,5")]
    [InlineData("Estimated kg", "8", "1e3")]
    [InlineData("Estimated kg", "8", "0")]
    [InlineData("Estimated kg", "8", "-1")]
    [InlineData("Estimated kg", "8", "1.234")]
    [InlineData("Estimated kg", "8", "abc")]
    [InlineData("Number of cages", "2147483647", null)] // cages are at most 100000 now: the estimate is never asked about
    [InlineData("Number of cages", "0", null)]
    [InlineData("Number of cages", "-1", null)]
    [InlineData("Number of cages", "8.555", null)]
    [InlineData("Number of cages", "8,5", null)]
    [InlineData("Number of cages", "abc", null)]
    [InlineData("Number of cages", null, null)]
    public async Task A_bad_cage_count_or_estimate_on_a_correction_changes_nothing(string field, string? cages, string? estimate)
    {
        var raft = await factory.SeededRaftAsync();
        var owner = raft.Owner.Client;
        var lot = await owner.HarvestLotAsync(raft.RaftId, HarvestFields.OfLanes(1));

        await AssertFieldRefusedAsync(await factory.SendCorrectionAsync(owner, lot.Id(), new HarvestFields { Cages = cages, EstimatedKg = estimate }), field);

        var read = await owner.GetHarvestLotAsync(lot.Id());
        Assert.Equal((8, 400m), (read.GetProperty("cageCount").GetDecimal(), read.Kg("estimatedKg")));
    }

    // The client list: "kgPerCage, lotCode, harvestedAt and harvestDate never change: sent values are ignored."
    [Fact]
    public async Task Values_a_correction_may_not_change_are_ignored_when_sent()
    {
        var raft = await factory.SeededRaftAsync();
        var owner = raft.Owner.Client;
        var lot = await owner.HarvestLotAsync(raft.RaftId, HarvestFields.OfLanes(1));
        var form = new HarvestCorrection(new HarvestFields { Cages = "9" }, "Wrong count", await factory.LotVersionAsync(lot.Id()), lot.PhotoKeysOfLot()).ToForm();
        form.Add(new StringContent("99"), "kgPerCage");
        form.Add(new StringContent("HACKED"), "lotCode");
        form.Add(new StringContent("2020-01-01"), "harvestDate");
        form.Add(new StringContent("2020-01-01T00:00:00Z"), "harvestedAt");
        form.Add(new StringContent(Guid.NewGuid().ToString()), "recordedByAccountId");

        var saved = (await (await owner.PutAsync($"/api/harvest-lots/{lot.Id()}", form)).ReadAsync(HttpStatusCode.OK)).GetProperty("lot");

        Assert.Equal(lot.Text("lotCode"), saved.Text("lotCode"));
        Assert.Equal(50m, saved.Kg("kgPerCage"));
        Assert.Equal(lot.Date("harvestDate"), saved.Date("harvestDate"));
        Assert.Equal(lot.GetProperty("harvestedAt").GetDateTimeOffset(), saved.GetProperty("harvestedAt").GetDateTimeOffset());
        Assert.Equal(lot.GetProperty("recordedByAccountId").GetGuid(), saved.GetProperty("recordedByAccountId").GetGuid());
    }

    // A lane first seeded after the date of the lot cannot join it, and the day itself counts: a lane seeded on the harvest date is fine (client list, Lanes). BR-ORIGIN-03 counts the days of a lot from that seed.
    [Fact]
    public async Task A_lane_seeded_on_the_harvest_date_can_be_added_and_one_seeded_the_day_after_cannot_also_with_an_older_reading_of_the_screen()
    {
        using var api = new CyclesQaClockApiFactory();
        var owner = (await api.OwnerAsync()).Client;
        api.Clock.At = HarvestLotCorrectionTestKit.InVietnam(10, 1);
        var zoneId = (await owner.CreateZoneAsync(NewZoneBody("KV91"))).Id();
        var raftId = (await owner.CreateRaftAsync(zoneId, NewRaftBody("B01", 3))).Id();
        var cycleId = (await owner.StartCycleAsync(raftId, new CycleBody(new DateOnly(2026, 10, 1)))).Id();
        var seed = await owner.SeedLotAsync(received: new DateOnly(2026, 10, 1));
        await owner.RecordSeedingAsync(cycleId, seed.Session([1], new DateOnly(2026, 10, 1)));
        api.Clock.At = HarvestLotCorrectionTestKit.InVietnam(10, 5);
        var lot = await owner.HarvestLotAsync(raftId, HarvestFields.OfLanes(1));
        await owner.RecordSeedingAsync(cycleId, seed.Session([2], new DateOnly(2026, 10, 5)));
        api.Clock.At = HarvestLotCorrectionTestKit.InVietnam(10, 6);
        await owner.RecordSeedingAsync(cycleId, seed.Session([3], new DateOnly(2026, 10, 6)));
        var screen = await owner.HarvestScreenAsync(raftId);
        Assert.True(screen.CanHarvest(2) && screen.CanHarvest(3));

        var refused = await (await api.SendCorrectionAsync(owner, lot.Id(), HarvestFields.OfLanes(1, 3))).ReadAsync(HttpStatusCode.BadRequest);
        Assert.Equal(("Lanes", "Lanes 3 cannot be harvested now."), (refused.Text("field"), refused.Text("hint")));

        var saved = (await api.CorrectLotAsync(owner, lot.Id(), HarvestFields.OfLanes(1, 2))).GetProperty("lot");
        Assert.Equal([1, 2], saved.Numbers("laneNumbers"));
        Assert.Equal(new DateOnly(2026, 10, 5), saved.Date("harvestDate"));
    }

    // BR-FARM-03: the state on a date comes from the records as they are now: a correction and a removal change the past of the views too, and nothing is left of what they took out.
    [Fact]
    public async Task The_progress_on_a_past_date_follows_a_correction_and_a_removal()
    {
        using var api = new CyclesQaClockApiFactory();
        var (owner, _, raftId, cycleId) = await api.ClockedRaftAsync("B01", "KV92");
        api.Clock.At = HarvestLotCorrectionTestKit.InVietnam(10, 5);
        var lot = await owner.HarvestLotAsync(raftId, HarvestFields.OfLanes(1).FinishedLanes(1));
        api.Clock.At = HarvestLotCorrectionTestKit.InVietnam(10, 10);

        async Task<string> StateAsync(int lane, string date) => (await owner.ProgressAsync(raftId, $"?date={date}")).GetProperty("lanes").EnumerateArray().Single(l => l.GetProperty("laneNumber").GetInt32() == lane).Text("state");

        Assert.Equal(("Growing", "Harvested"), (await StateAsync(1, "2026-10-04"), await StateAsync(1, "2026-10-05")));

        await api.CorrectLotAsync(owner, lot.Id(), HarvestFields.OfLanes(1, 2).FinishedLanes(2));

        Assert.Equal(("Growing", "BeingHarvested", "Harvested"), (await StateAsync(2, "2026-10-04"), await StateAsync(1, "2026-10-05"), await StateAsync(2, "2026-10-05")));
        Assert.Equal("BeingHarvested", (await owner.SeedingListAsync(cycleId)).StateOf(1));

        await api.RemoveLotAsync(owner, lot.Id());

        Assert.Equal(("Growing", "Growing"), (await StateAsync(1, "2026-10-05"), await StateAsync(2, "2026-10-10")));
    }

    // MSG-GEN-06 counts what is still in use: a lot corrected away or removed no longer counts.
    [Fact]
    public async Task A_refused_cancel_counts_the_lots_left_after_a_removal()
    {
        var raft = await factory.SeededRaftAsync();
        var owner = raft.Owner.Client;
        var (first, second) = (await owner.HarvestLotAsync(raft.RaftId, HarvestFields.OfLanes(1)), await owner.HarvestLotAsync(raft.RaftId, HarvestFields.OfLanes(2)));
        var before = await (await owner.CancelCycleAsync(raft.CycleId, "")).ReadAsync(HttpStatusCode.Conflict);

        await factory.RemoveLotAsync(owner, first.Id());

        var after = await (await owner.CancelCycleAsync(raft.CycleId, "")).ReadAsync(HttpStatusCode.Conflict);
        Assert.Equal(("MSG-GEN-06", "MSG-GEN-06"), (before.Text("code"), after.Text("code")));
        Assert.Contains("3 records", before.Text("detail"));
        Assert.Contains("2 records", after.Text("detail"));
        await factory.RemoveLotAsync(owner, second.Id());
    }

    // The close facts belong to the close (BR-HARVEST-04). Whether a correction after it should touch them is a PM call: see the report. This records what happens.
    [Fact]
    public async Task Observation_the_leftover_of_a_closed_lot_stays_as_closed_when_a_correction_lowers_the_estimate_below_it()
    {
        var raft = await factory.SeededRaftAsync();
        var owner = raft.Owner.Client;
        var lot = await owner.HarvestLotAsync(raft.RaftId, HarvestFields.OfLanes(1));
        await factory.UpdateAsync<HarvestLot>(lot.Id(), l =>
        {
            l.StatusId = StatusIds.HarvestLot.Closed;
            l.CloseDate = Today;
            l.LeftoverKg = 400m;
            l.CloseReason = HarvestLotCloseReason.TimeLimitReached;
        });

        var saved = (await factory.CorrectLotAsync(owner, lot.Id(), new HarvestFields { Lanes = ["1"], Cages = "2" })).GetProperty("lot");

        var figures = saved.GetProperty("figures");
        Assert.Equal((100m, 400m, 400m, 100m, -300m), (saved.Kg("estimatedKg"), saved.Kg("leftoverKg"), figures.Kg("lostKg"), figures.Kg("remainingKg"), figures.Kg("differenceKg")));
    }
}
