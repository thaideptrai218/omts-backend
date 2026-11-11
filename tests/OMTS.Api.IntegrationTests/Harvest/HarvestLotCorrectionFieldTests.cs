using System.Net;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using OMTS.Api.IntegrationTests.Cultivation;
using OMTS.Api.IntegrationTests.Farming;
using OMTS.Api.IntegrationTests.Infrastructure;
using OMTS.Domain.Entities.Harvest;
using static OMTS.Api.IntegrationTests.Harvest.HarvestLotCorrectionTestKit;

namespace OMTS.Api.IntegrationTests.Harvest;

/// <summary>HARVEST-01.1 steps 2 and 4: reason, cages and estimate, what never changes, the lanes a lot may hold, the version, and what the audit log keeps. Dates come from a clock each test sets.</summary>
public class HarvestLotCorrectionFieldTests
{
    [Fact]
    public async Task A_reason_is_required_on_a_correction_and_a_removal_and_every_invalid_field_is_listed()
    {
        using var api = new AuthApiFactory();
        var raft = await api.SeededRaftAsync();
        var owner = raft.Owner.Client;
        var lot = await owner.HarvestLotAsync(raft.RaftId);

        foreach (var reason in new string?[] { null, "   ", new('x', 1001) })
        {
            var correction = await (await api.SendCorrectionAsync(owner, lot.Id(), new HarvestFields(), reason)).ReadAsync(HttpStatusCode.BadRequest);
            Assert.Equal(("MSG-GEN-01", "Reason"), (correction.Text("code"), correction.Text("field")));
            var removal = await (await api.SendRemovalAsync(owner, lot.Id(), reason)).ReadAsync(HttpStatusCode.BadRequest);
            Assert.Equal(("MSG-GEN-01", "Reason"), (removal.Text("code"), removal.Text("field")));
        }

        var several = await (await api.SendCorrectionAsync(owner, lot.Id(), new HarvestFields { Lanes = [], Cages = "0", EstimatedKg = "abc" }, reason: null)).ReadAsync(HttpStatusCode.BadRequest);
        Assert.Equal(["Lanes", "Number of cages", "Estimated kg", "Reason"], several.GetProperty("fields").EnumerateArray().Select(f => f.GetString()));

        Assert.Equal(HttpStatusCode.OK, (await api.SendCorrectionAsync(owner, lot.Id(), new HarvestFields(), new string('x', 1000))).StatusCode);
    }

    // HARVEST-01.1 step 2 and BR-HARVEST-02: the lot's own kg per cage, never the setting of today, and a hand-changed estimate is not overwritten when the cages stay.
    [Fact]
    public async Task The_kg_per_cage_never_changes_and_the_estimate_follows_the_cages_of_the_lot_unless_sent()
    {
        using var api = new AuthApiFactory();
        var raft = await api.SeededRaftAsync();
        var owner = raft.Owner.Client;
        await owner.SetHarvestSettingsAsync(40m, 3);
        var lot = await owner.HarvestLotAsync(raft.RaftId, new HarvestFields { Cages = "10", EstimatedKg = "333" });
        await owner.SetHarvestSettingsAsync(62.5m, 3);

        var sameCages = (await api.CorrectLotAsync(owner, lot.Id(), new HarvestFields { Cages = "10" })).GetProperty("lot");
        Assert.Equal((40m, 333m), (sameCages.Kg("kgPerCage"), sameCages.Kg("estimatedKg")));

        var moreCages = (await api.CorrectLotAsync(owner, lot.Id(), new HarvestFields { Cages = "12" })).GetProperty("lot");
        Assert.Equal((40m, 480m), (moreCages.Kg("kgPerCage"), moreCages.Kg("estimatedKg")));

        var byHand = (await api.CorrectLotAsync(owner, lot.Id(), new HarvestFields { Cages = "15", EstimatedKg = "500.5" })).GetProperty("lot");
        Assert.Equal((15m, 40m, 500.5m), (byHand.GetProperty("cageCount").GetDecimal(), byHand.Kg("kgPerCage"), byHand.Kg("estimatedKg")));
    }

    [Fact]
    public async Task Cages_and_estimate_are_checked_as_on_a_record()
    {
        using var api = new AuthApiFactory();
        var raft = await api.SeededRaftAsync();
        var owner = raft.Owner.Client;
        var lot = await owner.HarvestLotAsync(raft.RaftId);

        foreach (var (fields, name) in new[]
        {
            (new HarvestFields { Cages = "0" }, "Number of cages"),
            (new HarvestFields { Cages = null }, "Number of cages"),
            (new HarvestFields { EstimatedKg = "0" }, "Estimated kg"),
            (new HarvestFields { EstimatedKg = "400,5" }, "Estimated kg"),
            (new HarvestFields { EstimatedKg = "1.234" }, "Estimated kg"),
            (new HarvestFields { Lanes = [] }, "Lanes"),
            (new HarvestFields { Lanes = ["1", "1"] }, "Lanes"),
            (new HarvestFields { Lanes = ["99"] }, "Lanes"),
            (new HarvestFields { Finished = ["2"] }, "Finished lanes"),
        })
        {
            var refused = await (await api.SendCorrectionAsync(owner, lot.Id(), fields)).ReadAsync(HttpStatusCode.BadRequest);
            Assert.Equal(("MSG-GEN-01", name), (refused.Text("code"), refused.Text("field")));
        }
    }

    // BR-HARVEST-03: the harvest date and time and the code never change, whatever is sent and whenever the correction comes.
    [Fact]
    public async Task The_lot_code_and_the_harvest_time_stay_on_a_later_day_and_a_sent_date_is_ignored()
    {
        using var api = new CyclesQaClockApiFactory();
        var (owner, _, raftId, _) = await api.ClockedRaftAsync();
        api.Clock.At = InVietnam(10, 26);
        var lot = await owner.HarvestLotAsync(raftId, HarvestFields.OfLanes(1));

        api.Clock.At = InVietnam(10, 28);
        var correction = new HarvestCorrection(new HarvestFields { Cages = "9" }, "Wrong count", await api.LotVersionAsync(lot.Id()));
        var form = correction.ToForm();
        form.Add(new StringContent("2026-10-01"), "harvestDate");
        form.Add(new StringContent("2026-10-01T00:00:00+07:00"), "harvestedAt");
        form.Add(new StringContent("OTHER-CODE"), "lotCode");
        form.Add(new StringContent("99"), "kgPerCage");
        var saved = (await (await owner.PutAsync($"/api/harvest-lots/{lot.Id()}", form)).ReadAsync(HttpStatusCode.OK)).GetProperty("lot");

        Assert.Equal("KV01-B01-20261026", saved.Text("lotCode"));
        Assert.Equal(new DateOnly(2026, 10, 26), saved.Date("harvestDate"));
        Assert.Equal(InVietnam(10, 26), saved.GetProperty("harvestedAt").GetDateTimeOffset());
        Assert.Equal(50m, saved.Kg("kgPerCage"));
        Assert.Equal(9, saved.GetProperty("cageCount").GetDecimal());
    }

    [Fact]
    public async Task Lanes_are_added_and_taken_out_and_the_states_are_worked_out_again()
    {
        using var api = new AuthApiFactory();
        var raft = await api.SeededRaftAsync();
        var owner = raft.Owner.Client;
        var other = await owner.HarvestLotAsync(raft.RaftId, HarvestFields.OfLanes(3));
        var lot = await owner.HarvestLotAsync(raft.RaftId, HarvestFields.OfLanes(1, 3).FinishedLanes(1));

        var saved = await api.CorrectLotAsync(owner, lot.Id(), HarvestFields.OfLanes(2, 4).FinishedLanes(4));

        Assert.Equal([2, 4], saved.GetProperty("lot").Numbers("laneNumbers"));
        Assert.Equal([4], saved.GetProperty("lot").Numbers("finishedLaneNumbers"));
        var screen = await owner.HarvestScreenAsync(raft.RaftId);
        Assert.Equal("Growing", screen.HarvestStateOf(1));
        Assert.Equal("BeingHarvested", screen.HarvestStateOf(2));
        Assert.Equal("BeingHarvested", screen.HarvestStateOf(3));
        Assert.Equal("Harvested", screen.HarvestStateOf(4));
        Assert.True(screen.CanHarvest(1));
        Assert.False(screen.CanHarvest(4));
        Assert.Equal([3], (await owner.GetHarvestLotAsync(other.Id())).Numbers("laneNumbers"));

        // Un-finishing gives the lane back.
        await api.CorrectLotAsync(owner, lot.Id(), HarvestFields.OfLanes(2, 4));
        Assert.Equal("BeingHarvested", (await owner.HarvestScreenAsync(raft.RaftId)).HarvestStateOf(4));
    }

    [Fact]
    public async Task A_lane_with_no_seed_or_finished_by_another_lot_cannot_be_added_with_a_hint()
    {
        using var api = new AuthApiFactory();
        var raft = await api.SeededRaftAsync(1, 2, 3);
        var owner = raft.Owner.Client;
        await owner.HarvestLotAsync(raft.RaftId, HarvestFields.OfLanes(2).FinishedLanes(2));
        var lot = await owner.HarvestLotAsync(raft.RaftId, HarvestFields.OfLanes(1));

        var notSeeded = await (await api.SendCorrectionAsync(owner, lot.Id(), HarvestFields.OfLanes(1, 4))).ReadAsync(HttpStatusCode.BadRequest);
        Assert.Equal(("Lanes", "Lanes 4 cannot be harvested now."), (notSeeded.Text("field"), notSeeded.Text("hint")));
        var finished = await (await api.SendCorrectionAsync(owner, lot.Id(), HarvestFields.OfLanes(1, 2))).ReadAsync(HttpStatusCode.BadRequest);
        Assert.Equal(("Lanes", "Lanes 2 cannot be harvested now."), (finished.Text("field"), finished.Text("hint")));
    }

    // The harvest date stays, so a lane first seeded after it was not there to harvest: the lot would come before its seed (BR-ORIGIN-03).
    [Fact]
    public async Task A_lane_seeded_after_the_harvest_date_cannot_be_added()
    {
        using var api = new CyclesQaClockApiFactory();
        var owner = (await api.OwnerAsync()).Client;
        api.Clock.At = InVietnam(10, 1);
        var zoneId = (await owner.CreateZoneAsync()).Id();
        var raftId = (await owner.CreateRaftAsync(zoneId)).Id();
        var cycleId = (await owner.StartCycleAsync(raftId, new CycleBody(new DateOnly(2026, 10, 1)))).Id();
        var seed = await owner.SeedLotAsync(received: new DateOnly(2026, 10, 1));
        await owner.RecordSeedingAsync(cycleId, seed.Session([1], new DateOnly(2026, 10, 1)));
        api.Clock.At = InVietnam(10, 26);
        var lot = await owner.HarvestLotAsync(raftId, HarvestFields.OfLanes(1));
        api.Clock.At = InVietnam(10, 27);
        await owner.RecordSeedingAsync(cycleId, seed.Session([2], new DateOnly(2026, 10, 27)));

        var refused = await (await api.SendCorrectionAsync(owner, lot.Id(), HarvestFields.OfLanes(1, 2))).ReadAsync(HttpStatusCode.BadRequest);

        Assert.Equal(("Lanes", "Lanes 2 cannot be harvested now."), (refused.Text("field"), refused.Text("hint")));
    }

    // BR-HARVEST-01: a lane the earlier lot holds stays on it after a later lot finishes it, so correcting its cages is not refused; marking it finished in the earlier lot is.
    [Fact]
    public async Task A_lane_a_later_lot_holds_stays_on_the_earlier_lot_but_cannot_be_marked_finished_there()
    {
        using var api = new CyclesQaClockApiFactory();
        var (owner, _, raftId, _) = await api.ClockedRaftAsync();
        api.Clock.At = InVietnam(10, 26);
        var first = await owner.HarvestLotAsync(raftId, HarvestFields.OfLanes(4, 5));
        api.Clock.At = InVietnam(10, 27);
        await owner.HarvestLotAsync(raftId, HarvestFields.OfLanes(4).FinishedLanes(4));
        await owner.HarvestLotAsync(raftId, HarvestFields.OfLanes(5));

        var cages = await api.CorrectLotAsync(owner, first.Id(), HarvestFields.OfLanes(4, 5) with { Cages = "9" });
        Assert.Equal(9, cages.GetProperty("lot").GetProperty("cageCount").GetDecimal());

        var refused = await (await api.SendCorrectionAsync(owner, first.Id(), HarvestFields.OfLanes(4, 5).FinishedLanes(5))).ReadAsync(HttpStatusCode.BadRequest);
        Assert.Equal(("Finished lanes", "Lanes 5 are in a later harvest lot and cannot be marked finished in this one."), (refused.Text("field"), refused.Text("hint")));
    }

    [Fact]
    public async Task A_stale_or_missing_version_saves_nothing()
    {
        using var api = new AuthApiFactory();
        var raft = await api.SeededRaftAsync();
        var owner = raft.Owner.Client;
        var lot = await owner.HarvestLotAsync(raft.RaftId);
        var current = await api.LotVersionAsync(lot.Id());

        var stale = await (await owner.PutHarvestLotAsync(lot.Id(), new HarvestCorrection(new HarvestFields { Cages = "9" }, RowVersion: AcsTestKit.StaleRowVersion))).ReadAsync(HttpStatusCode.Conflict);
        Assert.Equal(("MSG06", current), (stale.Text("code"), stale.GetProperty("currentRowVersion").GetString()));
        var missing = await (await owner.PutHarvestLotAsync(lot.Id(), new HarvestCorrection(new HarvestFields { Cages = "9" }))).ReadAsync(HttpStatusCode.BadRequest);
        Assert.Equal(("MSG-GEN-01", "RowVersion"), (missing.Text("code"), missing.Text("field")));
        Assert.Equal("MSG06", await (await owner.PostRemoveLotAsync(lot.Id(), "mistake", AcsTestKit.StaleRowVersion)).ErrorCodeAsync());
        Assert.Equal("RowVersion", (await (await owner.PostRemoveLotAsync(lot.Id(), "mistake", null)).ReadAsync(HttpStatusCode.BadRequest)).Text("field"));
        Assert.Equal(8, (await owner.GetHarvestLotAsync(lot.Id())).GetProperty("cageCount").GetDecimal());
    }

    // GBR-02 and HARVEST-01.1 step 4: the reason and the changed values are kept; the Owner is not told again; a correction that changes nothing writes nothing.
    [Fact]
    public async Task A_correction_keeps_the_reason_and_the_changed_values_and_tells_nobody()
    {
        using var api = new AuthApiFactory();
        var raft = await api.SeededRaftAsync();
        var manager = await api.ManagerAsync(raft.ZoneId);
        var lot = await manager.Client.HarvestLotAsync(raft.RaftId, new HarvestFields { Cages = "8" });
        Assert.Single(await api.NoticesAsync("HARVEST_LOT_RECORDED", lot.Id()));

        await api.CorrectLotAsync(manager.Client, lot.Id(), new HarvestFields { Cages = "8" });
        Assert.Equal(0, await api.QueryAsync(db => db.AuditEvents.CountAsync(e => e.Action == "HarvestLot.Corrected")));

        await api.CorrectLotAsync(manager.Client, lot.Id(), new HarvestFields { Cages = "9" });

        var row = await api.QueryAsync(db => db.AuditEvents.SingleAsync(e => e.Action == "HarvestLot.Corrected" && e.RecordId == lot.Id()));
        Assert.Equal(("Wrong count at sea", lot.Text("lotCode"), raft.ZoneId, manager.Account.Id), (row.Reason, row.RecordCode, row.ZoneId, row.ActorAccountId));
        using var after = JsonDocument.Parse(row.After!);
        Assert.Equal(9, after.RootElement.GetProperty("CageCount").GetInt32());
        Assert.Equal(450m, after.RootElement.GetProperty("EstimatedKg").GetDecimal());
        Assert.False(after.RootElement.TryGetProperty("LotCode", out _));
        Assert.Contains("\"CageCount\":8", row.Before!);
        Assert.Single(await api.NoticesAsync("HARVEST_LOT_RECORDED", lot.Id()));
    }

    // BR-HARVEST-05, BR-TRACE-05: a Published lot's page shows the correction at once and the audit row names the lot; a lot not published is not named.
    [Fact]
    public async Task A_correction_of_a_published_lot_is_named_in_the_audit_row_and_it_stays_published()
    {
        using var api = new AuthApiFactory();
        var raft = await api.SeededRaftAsync();
        var owner = raft.Owner.Client;
        var published = await owner.HarvestLotAsync(raft.RaftId);
        await api.UpdateAsync<HarvestLot>(published.Id(), l => l.PublishedAt = DateTimeOffset.UtcNow);
        var plain = await owner.HarvestLotAsync(raft.RaftId, HarvestFields.OfLanes(2));

        var saved = await api.CorrectLotAsync(owner, published.Id(), new HarvestFields { Cages = "9" });
        await api.CorrectLotAsync(owner, plain.Id(), HarvestFields.OfLanes(2) with { Cages = "9" });

        Assert.Equal("Published", saved.GetProperty("lot").Text("originState"));
        var rows = await api.QueryAsync(db => db.AuditEvents.Where(e => e.Action == "HarvestLot.Corrected").ToDictionaryAsync(e => e.RecordId!.Value, e => e.After!));
        Assert.Contains($"\"AffectedPublishedLotCodes\":[\"{published.Text("lotCode")}\"]", rows[published.Id()]);
        Assert.DoesNotContain("AffectedPublishedLotCodes", rows[plain.Id()]);
    }
}
