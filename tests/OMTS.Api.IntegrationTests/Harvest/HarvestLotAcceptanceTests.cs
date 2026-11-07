using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using OMTS.Api.IntegrationTests.Cultivation;
using OMTS.Api.IntegrationTests.Farming;
using OMTS.Api.IntegrationTests.Infrastructure;
using static OMTS.Api.IntegrationTests.Farming.FarmingTestKit;

namespace OMTS.Api.IntegrationTests.Harvest;

/// <summary>AC-03 in full on the days it names, BR-HARVEST-03 lot codes and the harvest date of UTC+7. The clock is set by each test, so a run may cross midnight.</summary>
public class HarvestLotAcceptanceTests
{
    private static DateTimeOffset InVietnam(int month, int day, int hour = 9, int minute = 0) => new(2026, month, day, hour, minute, 0, TimeSpan.FromHours(7));

    /// <summary>Zone KV01 and a raft with seed on every lane, at the start of October by the clock.</summary>
    private static async Task<(HttpClient Owner, Guid ZoneId, Guid RaftId, Guid CycleId)> KV01Async(CyclesQaClockApiFactory api, string raftCode, string zoneCode = "KV01")
    {
        var owner = (await api.OwnerAsync()).Client;
        api.Clock.At = InVietnam(10, 1);
        var zoneId = (await owner.CreateZoneAsync(NewZoneBody(zoneCode))).Id();
        var raftId = (await owner.CreateRaftAsync(zoneId, NewRaftBody(raftCode))).Id();
        var cycleId = (await owner.StartCycleAsync(raftId, new CycleBody(new DateOnly(2026, 10, 1)))).Id();
        var lot = await owner.SeedLotAsync(received: new DateOnly(2026, 10, 1));
        await owner.RecordSeedingAsync(cycleId, lot.Session([1, 2, 3, 4, 5], new DateOnly(2026, 10, 1)));
        return (owner, zoneId, raftId, cycleId);
    }

    [Fact]
    public async Task Ac_03_lane_4_is_in_two_lots_being_harvested_after_the_first_and_harvested_after_the_second()
    {
        using var api = new CyclesQaClockApiFactory();
        var (owner, _, raftId, cycleId) = await KV01Async(api, "KV01-B01");

        api.Clock.At = InVietnam(10, 26);
        var first = await owner.HarvestLotAsync(raftId, HarvestFields.OfLanes(4));

        Assert.Equal("KV01-B01-20261026", first.Text("lotCode"));
        Assert.Equal(InVietnam(10, 26), first.GetProperty("harvestedAt").GetDateTimeOffset());
        Assert.Equal(new DateOnly(2026, 10, 26), first.Date("harvestDate"));
        Assert.Equal((2901, "Open"), (first.GetProperty("statusId").GetInt32(), first.Text("status")));
        Assert.Equal("BeingHarvested", (await owner.HarvestScreenAsync(raftId)).HarvestStateOf(4));

        api.Clock.At = InVietnam(10, 27);
        var second = await owner.HarvestLotAsync(raftId, HarvestFields.OfLanes(4).FinishedLanes(4));

        Assert.Equal("KV01-B01-20261027", second.Text("lotCode"));
        var screen = await owner.HarvestScreenAsync(raftId);
        Assert.Equal("Harvested", screen.HarvestStateOf(4));
        Assert.False(screen.CanHarvest(4));
        Assert.Equal(["KV01-B01-20261027", "KV01-B01-20261026"], screen.LotCodes());

        // It cannot be chosen in another lot of the cycle, and the lanes beside it still can.
        var refused = await (await owner.PostHarvestAsync(raftId, HarvestFields.OfLanes(3, 4))).ReadAsync(HttpStatusCode.BadRequest);
        Assert.Equal(("MSG-GEN-01", "Lanes", "Lanes 4 cannot be harvested now."), (refused.Text("code"), refused.Text("field"), refused.Text("hint")));
        Assert.True(screen.CanHarvest(3));

        // CYCLE-05: both days keep their state, and the lane lists both lots.
        Assert.Equal("BeingHarvested", (await owner.ProgressAsync(raftId, "?date=2026-10-26")).GetProperty("lanes").EnumerateArray().Single(l => l.GetProperty("laneNumber").GetInt32() == 4).Text("state"));
        Assert.Equal("Growing", (await owner.ProgressAsync(raftId, "?date=2026-10-25")).GetProperty("lanes").EnumerateArray().Single(l => l.GetProperty("laneNumber").GetInt32() == 4).Text("state"));
        var lane = await owner.GetFromJsonAsync<JsonElement>($"/api/rafts/{raftId}/lanes/4/progress?cycleId={cycleId}");
        Assert.Equal(
            [("KV01-B01-20261027", true), ("KV01-B01-20261026", false)],
            lane.GetProperty("harvestLots").EnumerateArray().Select(l => (l.Text("lotCode"), l.GetProperty("isFinished").GetBoolean())));
    }

    [Fact]
    public async Task Ac_03_eight_cages_at_50_kg_are_400_which_the_manager_may_change_and_no_view_shows_kg_per_lane()
    {
        using var api = new CyclesQaClockApiFactory();
        var (owner, _, raftId, cycleId) = await KV01Async(api, "KV01-B01");
        api.Clock.At = InVietnam(10, 26);

        var worked = await owner.HarvestLotAsync(raftId, HarvestFields.OfLanes(1));
        Assert.Equal((8, 50m, 400m), (worked.GetProperty("cageCount").GetInt32(), worked.Kg("kgPerCage"), worked.Kg("estimatedKg")));

        var changed = await owner.HarvestLotAsync(raftId, HarvestFields.OfLanes(1) with { EstimatedKg = "385.5" });
        Assert.Equal((50m, 385.5m), (changed.Kg("kgPerCage"), changed.Kg("estimatedKg")));
        Assert.Equal(385.5m, changed.GetProperty("figures").Kg("remainingKg"));

        foreach (var lanes in new[]
        {
            (await owner.HarvestScreenAsync(raftId)).GetProperty("lanes"),
            (await owner.ProgressAsync(raftId)).GetProperty("lanes"),
            (await owner.GetFromJsonAsync<JsonElement>($"/api/rafts/{raftId}")).GetProperty("lanes"),
            (await owner.SeedingListAsync(cycleId)).GetProperty("lanes"),
        })
        {
            Assert.DoesNotContain("kg", lanes.GetRawText(), StringComparison.OrdinalIgnoreCase);
        }

        Assert.DoesNotContain("kg", (await owner.GetFromJsonAsync<JsonElement>($"/api/rafts/{raftId}/lanes/1/progress")).GetProperty("harvestLots").GetRawText(), StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("KV01", "KV01-B01", "KV01-B01-20261026")]
    [InlineData("KV01", "B01", "KV01-B01-20261026")]
    [InlineData("KV01", "KV01B01", "KV01-KV01B01-20261026")]
    [InlineData("KV1", "KV10-B01", "KV1-KV10-B01-20261026")]
    public async Task The_zone_code_comes_first_unless_the_raft_code_starts_with_it_and_the_second_lot_of_the_day_adds_2(string zoneCode, string raftCode, string code)
    {
        using var api = new CyclesQaClockApiFactory();
        var (owner, _, raftId, _) = await KV01Async(api, raftCode, zoneCode);
        api.Clock.At = InVietnam(10, 26);

        Assert.Equal(code, (await owner.HarvestLotAsync(raftId)).Text("lotCode"));
        Assert.Equal($"{code}-2", (await owner.HarvestLotAsync(raftId)).Text("lotCode"));
        Assert.Equal($"{code}-3", (await owner.HarvestLotAsync(raftId)).Text("lotCode"));
    }

    // BR-HARVEST-03: rafts B01 and KV01-B01 of one zone come to the same code: the code stays unique.
    [Fact]
    public async Task Two_rafts_that_build_the_same_code_get_two_codes()
    {
        using var api = new CyclesQaClockApiFactory();
        var (owner, zoneId, raftId, _) = await KV01Async(api, "B01");
        var other = (await owner.CreateRaftAsync(zoneId, NewRaftBody("KV01-B01"))).Id();
        var otherCycle = (await owner.StartCycleAsync(other, new CycleBody(new DateOnly(2026, 10, 1)))).Id();
        await owner.RecordSeedingAsync(otherCycle, (await owner.SeedLotAsync(received: new DateOnly(2026, 10, 1))).Session([1], new DateOnly(2026, 10, 1)));
        api.Clock.At = InVietnam(10, 26);

        Assert.Equal("KV01-B01-20261026", (await owner.HarvestLotAsync(raftId)).Text("lotCode"));
        Assert.Equal("KV01-B01-20261026-2", (await owner.HarvestLotAsync(other)).Text("lotCode"));
    }

    // "Today" is the date in UTC+7: 16:59 and 17:01 UTC are different harvest dates, and the code follows.
    [Fact]
    public async Task The_harvest_date_is_the_date_in_vietnam_a_minute_either_side_of_midnight()
    {
        using var api = new CyclesQaClockApiFactory();
        var (owner, _, raftId, _) = await KV01Async(api, "KV01-B01");

        api.Clock.At = InVietnam(10, 26, 23, 59);
        var before = await owner.HarvestLotAsync(raftId);
        api.Clock.At = InVietnam(10, 27, 0, 1);
        var after = await owner.HarvestLotAsync(raftId);

        Assert.Equal(("KV01-B01-20261026", new DateOnly(2026, 10, 26)), (before.Text("lotCode"), before.Date("harvestDate")));
        Assert.Equal(("KV01-B01-20261027", new DateOnly(2026, 10, 27)), (after.Text("lotCode"), after.Date("harvestDate")));
    }

    // The harvest date and time are the system's: a client sending its own is not heard.
    [Fact]
    public async Task A_harvest_date_sent_by_the_client_is_ignored()
    {
        using var api = new CyclesQaClockApiFactory();
        var (owner, _, raftId, _) = await KV01Async(api, "KV01-B01");
        api.Clock.At = InVietnam(10, 26);
        var form = new HarvestFields().ToForm();
        form.Add(new StringContent("2026-10-01"), "harvestDate");
        form.Add(new StringContent("2026-10-01T08:00:00+07:00"), "harvestedAt");

        var lot = (await (await owner.PostAsync($"/api/rafts/{raftId}/harvest-lots", form)).ReadAsync(HttpStatusCode.Created)).GetProperty("lot");

        Assert.Equal(new DateOnly(2026, 10, 26), lot.Date("harvestDate"));
        Assert.Equal(InVietnam(10, 26), lot.GetProperty("harvestedAt").GetDateTimeOffset());
    }

    // BR-HARVEST-03 "unique ... never changes": a lot whose raft code was changed later keeps its code.
    [Fact]
    public async Task The_saved_lot_keeps_its_code_and_is_audited_with_the_values()
    {
        using var api = new CyclesQaClockApiFactory();
        var (owner, zoneId, raftId, _) = await KV01Async(api, "KV01-B01");
        api.Clock.At = InVietnam(10, 26);

        var lot = await owner.HarvestLotAsync(raftId, HarvestFields.OfLanes(1, 2).FinishedLanes(2));

        var audit = await api.QueryAsync(db => db.AuditEvents.SingleAsync(e => e.Action == "HarvestLot.Created" && e.RecordId == lot.Id()));
        Assert.Equal(("HarvestLot", "KV01-B01-20261026", zoneId), (audit.RecordType, audit.RecordCode, audit.ZoneId));
        using var after = JsonDocument.Parse(audit.After!);
        Assert.Equal([1, 2], after.RootElement.GetProperty("Lanes").EnumerateArray().Select(n => n.GetInt32()));
        Assert.Equal([2], after.RootElement.GetProperty("FinishedLanes").EnumerateArray().Select(n => n.GetInt32()));
        Assert.Equal(400m, after.RootElement.GetProperty("EstimatedKg").GetDecimal());
    }
}
