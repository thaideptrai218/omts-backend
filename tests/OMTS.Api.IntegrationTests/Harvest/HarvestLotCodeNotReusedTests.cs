using Microsoft.EntityFrameworkCore;
using OMTS.Api.IntegrationTests.Cultivation;
using OMTS.Api.IntegrationTests.Farming;
using OMTS.Api.IntegrationTests.Infrastructure;
using OMTS.Domain.Entities.Platform;
using static OMTS.Api.IntegrationTests.Harvest.HarvestLotCorrectionTestKit;

namespace OMTS.Api.IntegrationTests.Harvest;

/// <summary>PM default on BR-HARVEST-03: the code of a removed lot is not given again. Every code here is exact, on the days of a clock each test sets.</summary>
public class HarvestLotCodeNotReusedTests
{
    private const string Base = "KV01-B01-20261026";

    private static async Task<string> NextCodeAsync(HttpClient owner, Guid raftId, int lane = 1) =>
        (await owner.HarvestLotAsync(raftId, HarvestFields.OfLanes(lane))).Text("lotCode");

    [Fact]
    public async Task Removing_the_top_lot_does_not_free_its_code()
    {
        using var api = new CyclesQaClockApiFactory();
        var (owner, _, raftId, _) = await api.ClockedRaftAsync();
        api.Clock.At = InVietnam(10, 26);
        await NextCodeAsync(owner, raftId);
        var second = await owner.HarvestLotAsync(raftId, HarvestFields.OfLanes(1));
        Assert.Equal($"{Base}-2", second.Text("lotCode"));

        await api.RemoveLotAsync(owner, second.Id());

        Assert.Equal($"{Base}-3", await NextCodeAsync(owner, raftId));
    }

    [Fact]
    public async Task Removing_the_only_lot_of_the_day_leaves_the_next_on_minus_2_and_removing_all_never_starts_over()
    {
        using var api = new CyclesQaClockApiFactory();
        var (owner, _, raftId, _) = await api.ClockedRaftAsync();
        api.Clock.At = InVietnam(10, 26);
        var first = await owner.HarvestLotAsync(raftId, HarvestFields.OfLanes(1));
        Assert.Equal(Base, first.Text("lotCode"));

        await api.RemoveLotAsync(owner, first.Id());
        var second = await owner.HarvestLotAsync(raftId, HarvestFields.OfLanes(1));
        Assert.Equal($"{Base}-2", second.Text("lotCode"));

        await api.RemoveLotAsync(owner, second.Id());
        Assert.Equal($"{Base}-3", await NextCodeAsync(owner, raftId));
    }

    [Fact]
    public async Task Removing_a_lot_in_the_middle_leaves_the_others_and_the_next_goes_on()
    {
        using var api = new CyclesQaClockApiFactory();
        var (owner, _, raftId, _) = await api.ClockedRaftAsync();
        api.Clock.At = InVietnam(10, 26);
        var first = await owner.HarvestLotAsync(raftId, HarvestFields.OfLanes(1));
        var second = await owner.HarvestLotAsync(raftId, HarvestFields.OfLanes(2));
        var third = await owner.HarvestLotAsync(raftId, HarvestFields.OfLanes(3));

        await api.RemoveLotAsync(owner, second.Id());

        Assert.Equal($"{Base}-4", await NextCodeAsync(owner, raftId, 4));
        Assert.Equal(first.Text("lotCode"), (await owner.GetHarvestLotAsync(first.Id())).Text("lotCode"));
        Assert.Equal(third.Text("lotCode"), (await owner.GetHarvestLotAsync(third.Id())).Text("lotCode"));
    }

    // A correction neither gives nor frees a code, also when it adds a lane or moves the lot past another of that day.
    [Fact]
    public async Task A_correction_keeps_the_code_and_the_next_lot_goes_on_from_it()
    {
        using var api = new CyclesQaClockApiFactory();
        var (owner, _, raftId, _) = await api.ClockedRaftAsync();
        api.Clock.At = InVietnam(10, 26);
        var first = await owner.HarvestLotAsync(raftId, HarvestFields.OfLanes(1));

        var saved = await api.CorrectLotAsync(owner, first.Id(), HarvestFields.OfLanes(1, 2).FinishedLanes(2));

        Assert.Equal(Base, saved.GetProperty("lot").Text("lotCode"));
        Assert.Equal($"{Base}-2", await NextCodeAsync(owner, raftId));
    }

    // Each harvest date starts its own codes; a removal yesterday does not reach today.
    [Fact]
    public async Task Another_day_starts_clean_after_a_removal()
    {
        using var api = new CyclesQaClockApiFactory();
        var (owner, _, raftId, _) = await api.ClockedRaftAsync();
        api.Clock.At = InVietnam(10, 26);
        var first = await owner.HarvestLotAsync(raftId, HarvestFields.OfLanes(1));
        await api.RemoveLotAsync(owner, first.Id());

        api.Clock.At = InVietnam(10, 27);

        Assert.Equal("KV01-B01-20261027", await NextCodeAsync(owner, raftId));
    }

    // Rafts B01 and KV01-B01 of one zone come to one code: a code one of them gave and removed is closed to the other.
    [Fact]
    public async Task A_code_removed_on_one_raft_is_closed_to_the_raft_that_shares_it()
    {
        using var api = new CyclesQaClockApiFactory();
        var (owner, zoneId, plainRaft, _) = await api.ClockedRaftAsync("B01");
        var prefixedRaft = (await owner.CreateRaftAsync(zoneId, FarmingTestKit.NewRaftBody("KV01-B01"))).Id();
        var cycleId = (await owner.StartCycleAsync(prefixedRaft, new CycleBody(new DateOnly(2026, 10, 1)))).Id();
        var seed = await owner.SeedLotAsync(received: new DateOnly(2026, 10, 1));
        await owner.RecordSeedingAsync(cycleId, seed.Session([1], new DateOnly(2026, 10, 1)));
        api.Clock.At = InVietnam(10, 26);
        var first = await owner.HarvestLotAsync(plainRaft, HarvestFields.OfLanes(1));
        Assert.Equal(Base, first.Text("lotCode"));

        await api.RemoveLotAsync(owner, first.Id());

        Assert.Equal($"{Base}-2", await NextCodeAsync(owner, prefixedRaft));
    }

    // Only a code given counts: not a longer text that starts the same way, nor another action on a code.
    [Fact]
    public async Task Audit_rows_that_gave_no_such_code_do_not_count()
    {
        using var api = new CyclesQaClockApiFactory();
        var (owner, _, raftId, _) = await api.ClockedRaftAsync();
        api.Clock.At = InVietnam(10, 26);
        await api.UseDbAsync(db =>
        {
            db.AuditEvents.Add(new AuditEvent { OccurredAt = DateTimeOffset.UtcNow, Action = "HarvestLot.Created", RecordType = "HarvestLot", RecordCode = $"{Base}X-9" });
            db.AuditEvents.Add(new AuditEvent { OccurredAt = DateTimeOffset.UtcNow, Action = "HarvestLot.Corrected", RecordType = "HarvestLot", RecordCode = $"{Base}-7" });
        });

        Assert.Equal(Base, await NextCodeAsync(owner, raftId));
        Assert.Equal(1, await api.QueryAsync(db => db.AuditEvents.CountAsync(e => e.Action == "HarvestLot.Created" && e.RecordCode == Base)));
    }
}
