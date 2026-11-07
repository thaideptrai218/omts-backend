using System.Net;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using OMTS.Api.IntegrationTests.Farming;
using OMTS.Api.IntegrationTests.Infrastructure;
using static OMTS.Api.IntegrationTests.Farming.FarmingTestKit;

namespace OMTS.Api.IntegrationTests.Cultivation;

/// <summary>CYCLE-02.1 and BR-CYCLE-02: mark the seeding complete once per cycle, the Owner told once (MSG-CYCLE-02), and what can be seeded before and after.</summary>
public class SeedingCompletionTests(AuthApiFactory factory) : IClassFixture<AuthApiFactory>
{
    [Fact]
    public async Task The_question_lists_the_lanes_still_not_seeded_and_only_then_has_its_second_sentence()
    {
        var setup = await factory.CycleAsync();
        var owner = setup.Owner.Client;
        var lot = await owner.SeedLotAsync();
        await owner.RecordSeedingAsync(setup.CycleId, lot.Session([1, 2, 4]));
        var raftCode = (await owner.SeedingListAsync(setup.CycleId)).Text("raftCode");

        var some = await (await owner.CompletionPreviewAsync(setup.CycleId)).ReadAsync(HttpStatusCode.OK);

        Assert.Equal("MSG-CYCLE-01", some.Text("messageCode"));
        Assert.Equal($"Finish seeding of raft {raftCode}? 2 lane(s) have no seed yet (lanes 3, 5); you can still seed them later. The Cooperative Owner will be asked to review the seeding.", some.Text("message"));
        Assert.Equal((5, 3), (some.GetProperty("laneCount").GetInt32(), some.GetProperty("seededCount").GetInt32()));
        Assert.Equal([3, 5], some.Numbers("notSeededLanes"));

        await owner.RecordSeedingAsync(setup.CycleId, lot.Session([3, 5]));
        var none = await (await owner.CompletionPreviewAsync(setup.CycleId)).ReadAsync(HttpStatusCode.OK);
        Assert.Equal($"Finish seeding of raft {raftCode}? The Cooperative Owner will be asked to review the seeding.", none.Text("message"));
    }

    [Fact]
    public async Task Marking_complete_saves_today_tells_the_owners_once_and_a_second_mark_is_refused()
    {
        var setup = await factory.CycleAsync();
        var manager = await factory.ManagerAsync(setup.ZoneId);
        var lot = await setup.Owner.Client.SeedLotAsync();
        await manager.Client.RecordSeedingAsync(setup.CycleId, lot.Session([1, 2, 3]));

        var marked = await manager.Client.MarkSeedingCompleteAsync(setup.CycleId);

        Assert.Equal(Today, marked.GetProperty("cycle").Date("seedingCompletedOn"));
        Assert.Equal((5, 3), (marked.GetProperty("laneCount").GetInt32(), marked.GetProperty("seededCount").GetInt32()));
        Assert.Equal([4, 5], marked.Numbers("notSeededLanes"));
        var raftCode = marked.GetProperty("cycle").Text("raftCode");
        var rows = await factory.SeedingNoticesAsync(setup.CycleId);
        Assert.Contains(rows, n => n.RecipientAccountId == setup.Owner.Account.Id);
        Assert.DoesNotContain(rows, n => n.RecipientAccountId == manager.Account.Id);
        Assert.Equal(rows.Count, rows.Select(n => n.RecipientAccountId!.Value).Distinct().Count());
        Assert.All(rows, n =>
        {
            Assert.Equal(("MSG-CYCLE-02", false, "CultivationCycle", raftCode), (n.MessageCode, n.IsMandatory, n.RelatedRecordType, n.RelatedRecordCode));
            Assert.Equal($"Raft {raftCode}: seeding is finished; 3 of 5 lanes have seed. Please review the seeding sessions.", n.Body);
            using var parameters = JsonDocument.Parse(n.MessageParams!);
            Assert.Equal((raftCode, 3, 5), (parameters.RootElement.Text("raft_code"), parameters.RootElement.GetProperty("seeded_count").GetInt32(), parameters.RootElement.GetProperty("lane_count").GetInt32()));
        });
        Assert.Equal(1, await factory.QueryAsync(db => db.AuditEvents.CountAsync(e => e.Action == "CultivationCycle.SeedingCompleted" && e.RecordId == setup.CycleId)));

        var list = await manager.Client.SeedingListAsync(setup.CycleId);
        Assert.False(list.Flag("canMarkComplete"));
        Assert.Equal("MSG-GEN-09", await (await manager.Client.PostSeedingCompleteAsync(setup.CycleId, list.Text("cycleRowVersion"))).ErrorCodeAsync());
        Assert.Equal("MSG-GEN-09", await (await manager.Client.CompletionPreviewAsync(setup.CycleId)).ErrorCodeAsync());
        Assert.Equal(rows.Count, (await factory.SeedingNoticesAsync(setup.CycleId)).Count);
        Assert.Equal(1, await factory.QueryAsync(db => db.AuditEvents.CountAsync(e => e.Action == "CultivationCycle.SeedingCompleted" && e.RecordId == setup.CycleId)));
    }

    [Fact]
    public async Task An_owner_is_not_told_of_their_own_mark_and_another_owner_is()
    {
        var setup = await factory.CycleAsync();
        var other = await factory.OwnerAsync();

        await setup.Owner.Client.MarkSeedingCompleteAsync(setup.CycleId);

        var told = (await factory.SeedingNoticesAsync(setup.CycleId)).Select(n => n.RecipientAccountId!.Value).ToList();
        Assert.DoesNotContain(setup.Owner.Account.Id, told);
        Assert.Contains(other.Account.Id, told);
    }

    // BR-CYCLE-02: a Growing lane may be chosen again until the seeding is marked complete; after it only Not seeded lanes, with no new notification.
    [Fact]
    public async Task Before_the_mark_a_growing_lane_can_be_hung_again_and_after_it_only_lanes_not_seeded_with_no_new_notice()
    {
        var setup = await factory.CycleAsync();
        var manager = await factory.ManagerAsync(setup.ZoneId);
        var lot = await setup.Owner.Client.SeedLotAsync();
        var other = await setup.Owner.Client.SeedLotAsync();
        var first = await manager.Client.SeedingRecordAsync(setup.CycleId, lot.Session([1, 2]));

        // Re-hanging: the lane traces back to both lots.
        await manager.Client.RecordSeedingAsync(setup.CycleId, other.Session([2, 3]));
        var before = await manager.Client.SeedingListAsync(setup.CycleId);
        Assert.Equal((true, true, true), (LaneFlag(before, 1), LaneFlag(before, 2), LaneFlag(before, 4)));

        await manager.Client.MarkSeedingCompleteAsync(setup.CycleId);
        var told = (await factory.SeedingNoticesAsync(setup.CycleId)).Count;

        var after = await manager.Client.SeedingListAsync(setup.CycleId);
        Assert.Equal((false, true), (LaneFlag(after, 2), LaneFlag(after, 4)));
        Assert.True(after.Flag("canRecord"));

        var refused = await (await manager.Client.PostSeedingAsync(setup.CycleId, lot.Session([2, 4]))).ReadAsync(HttpStatusCode.BadRequest);
        Assert.Equal(("MSG-GEN-01", "Lanes", "Lanes 2 cannot be seeded now."), (refused.Text("code"), refused.Text("field"), refused.Text("hint")));

        await manager.Client.RecordSeedingAsync(setup.CycleId, lot.Session([4, 5]));
        Assert.Equal(told, (await factory.SeedingNoticesAsync(setup.CycleId)).Count);

        // A correction keeps its own lanes, and may give one back, but cannot take a lane with seed.
        var kept = first.AsCorrection() with { Quantity = 3m, RowVersion = (await manager.Client.GetSeedingAsync(first.Id())).Text("rowVersion") };
        await (await manager.Client.PutSeedingAsync(first.Id(), kept)).ReadAsync(HttpStatusCode.OK);
        Assert.Equal(["Lanes"], await (await manager.Client.PutSeedingAsync(first.Id(), kept with { LaneNumbers = [1, 2, 3] })).InvalidFieldsAsync());
        await (await manager.Client.PutSeedingAsync(first.Id(), kept with { LaneNumbers = [1] })).ReadAsync(HttpStatusCode.OK);
    }

    [Fact]
    public async Task The_offer_to_mark_comes_when_no_lane_in_use_is_left_not_seeded_and_not_after_the_mark()
    {
        var setup = await factory.CycleAsync();
        var owner = setup.Owner.Client;
        var lot = await owner.SeedLotAsync();

        Assert.False((await owner.RecordSeedingAsync(setup.CycleId, lot.Session([1, 2, 3, 4]))).Flag("offerMarkComplete"));
        Assert.True((await owner.RecordSeedingAsync(setup.CycleId, lot.Session([4, 5]))).Flag("offerMarkComplete"));

        var other = await factory.CycleAsync();
        await other.Owner.Client.MarkSeedingCompleteAsync(other.CycleId);
        var lotOfOther = await other.Owner.Client.SeedLotAsync();
        Assert.False((await other.Owner.Client.RecordSeedingAsync(other.CycleId, lotOfOther.Session([1, 2, 3, 4, 5]))).Flag("offerMarkComplete"));
    }

    // ACCOUNT-08: opening checks the access again; a cycle removed after its sessions is gone.
    [Fact]
    public async Task The_notice_opens_for_the_owner_and_says_gone_after_the_cycle_is_cancelled()
    {
        var setup = await factory.CycleAsync();
        var manager = await factory.ManagerAsync(setup.ZoneId);
        await manager.Client.MarkSeedingCompleteAsync(setup.CycleId);
        var row = (await factory.SeedingNoticesAsync(setup.CycleId)).Single(n => n.RecipientAccountId == setup.Owner.Account.Id);
        var path = $"/api/me/notifications/{row.Id}/open";

        await (await setup.Owner.Client.GetAsync(path)).ReadAsync(HttpStatusCode.OK);

        await (await manager.Client.CancelCycleAsync(setup.CycleId, (await manager.Client.SeedingListAsync(setup.CycleId)).Text("cycleRowVersion"))).ReadAsync(HttpStatusCode.NoContent);
        Assert.Equal((HttpStatusCode.NotFound, "MSG-ACCOUNT-22"), await ReadCodeAsync(await setup.Owner.Client.GetAsync(path)));
    }

    private static bool LaneFlag(JsonElement list, int number) =>
        list.GetProperty("lanes").EnumerateArray().Single(l => l.GetProperty("laneNumber").GetInt32() == number).Flag("canSeed");

    private static async Task<(HttpStatusCode, string?)> ReadCodeAsync(HttpResponseMessage response) => (response.StatusCode, await response.ErrorCodeAsync());
}
