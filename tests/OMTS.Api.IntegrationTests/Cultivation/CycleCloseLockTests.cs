using System.Net;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using OMTS.Api.IntegrationTests.Farming;
using OMTS.Api.IntegrationTests.Harvest;
using OMTS.Api.IntegrationTests.Infrastructure;
using OMTS.Domain.Enums;
using static OMTS.Api.IntegrationTests.Farming.FarmingTestKit;
using static OMTS.Api.IntegrationTests.Infrastructure.AcsTestKit;

namespace OMTS.Api.IntegrationTests.Cultivation;

/// <summary>
/// BR-CYCLE-04 and BR-HARVEST-05 after a real close (CYCLE-06 POST-2): every write of the cultivation and harvest screens on the cycle answers MSG-GEN-09 and changes nothing;
/// only TRACE-03 (PR T2) may still change its records. The records stay readable, and the lots stay for sale (BR-HARVEST-04).
/// </summary>
public class CycleCloseLockTests(AuthApiFactory factory) : IClassFixture<AuthApiFactory>
{
    private static async Task AssertLockedAsync(Task<HttpResponseMessage> write, string what)
    {
        var answer = await write;
        var body = await answer.Content.ReadAsStringAsync();
        Assert.True(answer.StatusCode == HttpStatusCode.Conflict, $"{what}: {(int)answer.StatusCode} {body}");
        Assert.True(body.Contains("\"MSG-GEN-09\"", StringComparison.Ordinal), $"{what}: {body}");
    }

    [Fact]
    public async Task After_the_close_every_cycle_write_answers_msg_gen_09_and_changes_nothing()
    {
        var raft = await factory.SeededRaftAsync();
        var owner = raft.Owner.Client;
        var item = await factory.ItemAsync();
        var log = await owner.RecordLogAsync(raft.CycleId);
        var usage = await owner.RecordUsageAsync(raft.RaftId, item, Today.AddDays(-1));
        var lot = await owner.HarvestLotAsync(raft.RaftId, HarvestFields.OfLanes(1));
        var cycle = await owner.GetFromJsonAsync<System.Text.Json.JsonElement>($"/api/cultivation-cycles/{raft.CycleId}");

        await owner.CloseCycleAsync(raft.CycleId);
        var before = await CountsAsync(raft.CycleId, raft.RaftId);

        // CYCLE-02: sessions, their correction and removal, and the mark complete.
        await AssertLockedAsync(owner.PostSeedingAsync(raft.CycleId, raft.Lot.Session([3])), "new session");
        await AssertLockedAsync(owner.PutSeedingAsync(raft.Session.Id(), raft.Session.AsCorrection() with { Quantity = 7m }), "session correction");
        await AssertLockedAsync(owner.RemoveSeedingAsync(raft.Session.Id(), rowVersion: raft.Session.Text("rowVersion")), "session removal");
        await AssertLockedAsync(owner.PostSeedingCompleteAsync(raft.CycleId, cycle.Text("rowVersion")), "mark complete");

        // CYCLE-04: logs, their correction and removal.
        await AssertLockedAsync(owner.PostLogAsync(raft.CycleId), "new log");
        var logVersion = await factory.LogVersionAsync(log.Id());
        await AssertLockedAsync(owner.PutLogAsync(log.Id(), new LogFields { Notes = "late", Reason = "late note", RowVersion = logVersion, KeepPhotoKeys = log.PhotoKeys(), Photos = [] }), "log correction");
        await AssertLockedAsync(owner.RemoveLogAsync(log.Id(), "late", logVersion), "log removal");

        // CYCLE-03: a usage dated within the cycle, the close day included, and the edit of one.
        await AssertLockedAsync(owner.PostUsageAsync(raft.RaftId, new UsageBody(Today, MaterialUsageTestKit.BrokenBuoy, [new UsageLine(item)])), "usage on the close day");
        await AssertLockedAsync(owner.PutUsageAsync(usage.Id(), usage.AsEdit() with { Quantity = 9m }), "usage edit");

        // HARVEST-01 and HARVEST-01.1: a new lot, the correction and the removal of one.
        await AssertLockedAsync(owner.PostHarvestAsync(raft.RaftId, HarvestFields.OfLanes(2)), "new lot");
        await AssertLockedAsync(factory.SendCorrectionAsync(owner, lot.Id(), HarvestFields.OfLanes(1) with { Cages = "3" }), "lot correction");
        await AssertLockedAsync(factory.SendRemovalAsync(owner, lot.Id()), "lot removal");

        // CYCLE-01 and CYCLE-06: the plan, the cancel and a second close; a closed cycle is never reopened.
        var closed = await owner.GetFromJsonAsync<System.Text.Json.JsonElement>($"/api/cultivation-cycles/{raft.CycleId}");
        await AssertLockedAsync(owner.PutCycleAsync(raft.CycleId, new CycleBody(Today.AddDays(-10), 1m, RowVersion: closed.Text("rowVersion"))), "plan edit");
        await AssertLockedAsync(owner.CancelCycleAsync(raft.CycleId, closed.Text("rowVersion")), "cancel");
        await AssertLockedAsync(owner.PostCloseAsync(raft.CycleId, "again", closed.Text("rowVersion")), "second close");
        await AssertLockedAsync(owner.GetClosePreviewAsync(raft.CycleId), "close preview");

        Assert.Equal(before, await CountsAsync(raft.CycleId, raft.RaftId));
        Assert.Single(await factory.CloseAuditAftersAsync(raft.CycleId));

        // Viewing still works, and the lot stays for sale: the close locks cultivation records, not sales.
        Assert.Equal(HttpStatusCode.OK, (await owner.GetAsync($"/api/cultivation-logs/{log.Id()}")).StatusCode);
        Assert.Contains(lot.Text("lotCode"), (await owner.LotsForSaleAsync(raft.ZoneId)).ForSaleCodes());
    }

    [Fact]
    public async Task The_closed_views_show_the_summary_with_the_assessment_and_the_lane_states_on_the_close_date()
    {
        var raft = await factory.SeededRaftAsync(1, 2);
        var owner = raft.Owner.Client;
        await owner.HarvestLotAsync(raft.RaftId, HarvestFields.OfLanes(1, 2).FinishedLanes(1) with { EstimatedKg = "123.4" });
        var closed = await owner.CloseCycleAsync(raft.CycleId, "Good spat, lane 2 slow");

        var summary = await owner.CycleSummaryAsync(raft.CycleId);
        Assert.Equal(closed.GetProperty("summary").GetRawText(), summary.GetRawText());
        Assert.Equal(("Good spat, lane 2 slow", Today, 123.4m, 10m, 1), (summary.Text("assessment"), summary.Date("asOf"), summary.Number("kgHarvested"), summary.Number("seedHung"), summary.GetProperty("seedingSessionCount").GetInt32()));
        Assert.Equal([2], summary.Numbers("notHarvestedLanes"));
        Assert.True(summary.Flag("closedEarly"));

        // CYCLE-05.1 and 05.2: the progress of the closed cycle shows its close date, its summary and its assessment.
        var progress = await owner.ProgressAsync(raft.RaftId, $"?cycleId={raft.CycleId}");
        Assert.Equal(Today, progress.Date("date"));
        Assert.Equal(summary.GetRawText(), progress.GetProperty("summary").GetRawText());
        Assert.Equal(("Harvested", "BeingHarvested", "NotSeeded"), (StateOf(progress, 1), StateOf(progress, 2), StateOf(progress, 3)));
        Assert.Equal("Good spat, lane 2 slow", progress.GetProperty("cycle").Text("assessment"));

        // The progress of an Open cycle has no summary, as before.
        var other = await factory.SeededRaftAsync();
        Assert.True((await other.Owner.Client.ProgressAsync(other.RaftId)).IsNull("summary"));
    }

    [Fact]
    public async Task The_summary_of_an_open_cycle_counts_to_today_and_shows_what_a_close_would_be()
    {
        var raft = await factory.SeededRaftAsync();
        var summary = await raft.Owner.Client.CycleSummaryAsync(raft.CycleId);

        Assert.Equal((Today, true, "LanesNotHarvested", 11), (summary.Date("asOf"), summary.Flag("closedEarly"), summary.Text("earlyReason"), summary.GetProperty("daysOfFarming").GetInt32()));
        Assert.True(summary.IsNull("assessment"));
        Assert.True(summary.IsNull("closeDate"));
        Assert.Equal([1, 2, 3, 4, 5], summary.Numbers("notHarvestedLanes"));
    }

    [Fact]
    public async Task Only_a_zone_manager_of_the_zone_and_the_owner_close_and_the_others_change_nothing()
    {
        var raft = await factory.SeededRaftAsync();
        var elsewhere = await factory.ManagerAsync((await raft.Owner.Client.CreateZoneAsync()).Id());
        var accountant = await factory.StaffAsync(null, StaffRole.Accountant);
        var version = (await raft.Owner.Client.ClosePreviewAsync(raft.CycleId)).Text("cycleRowVersion");

        foreach (var caller in new[] { elsewhere.Client, accountant.Client })
        {
            Assert.Equal(HttpStatusCode.Forbidden, (await caller.GetClosePreviewAsync(raft.CycleId)).StatusCode);
            var refused = await caller.PostCloseAsync(raft.CycleId, CycleCloseTestKit.Assessment, version, ownerApproved: true);
            Assert.Equal((HttpStatusCode.Forbidden, "MSG-GEN-08"), (refused.StatusCode, await refused.ErrorCodeAsync()));
            Assert.Equal(HttpStatusCode.Forbidden, (await caller.GetAsync($"/api/cultivation-cycles/{raft.CycleId}/summary")).StatusCode);
        }

        Assert.Equal(HttpStatusCode.NotFound, (await raft.Owner.Client.GetClosePreviewAsync(Guid.NewGuid())).StatusCode);
        Assert.Empty(await factory.CloseAuditAftersAsync(raft.CycleId));
        var manager = await factory.ManagerAsync(raft.ZoneId);
        Assert.Equal(HttpStatusCode.OK, (await manager.Client.SendCloseAsync(raft.CycleId, ownerApproved: true)).StatusCode);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("   ")]
    [InlineData("long")]
    public async Task Without_a_valid_assessment_the_cycle_is_not_closed(string? assessment)
    {
        var raft = await factory.SeededRaftAsync();
        var text = assessment == "long" ? new string('a', 2001) : assessment;

        var refused = await raft.Owner.Client.SendCloseAsync(raft.CycleId, text);

        var body = await refused.ReadAsync(HttpStatusCode.BadRequest);
        Assert.Equal(("MSG-GEN-01", "Assessment"), (body.Text("code"), body.Text("field")));
        Assert.Empty(await factory.CloseAuditAftersAsync(raft.CycleId));
        Assert.Equal(Domain.Constants.StatusIds.CultivationCycle.Open,
            await factory.QueryAsync(db => db.CultivationCycles.Where(c => c.Id == raft.CycleId).Select(c => c.StatusId).SingleAsync()));
    }

    [Fact]
    public async Task A_close_with_an_older_version_answers_msg06_and_changes_nothing()
    {
        var raft = await factory.SeededRaftAsync();
        // The current version ends 7, n with n of 1 to 254: this older one can never match it.
        await factory.IssueRowVersionAsync<Domain.Entities.Cultivation.CultivationCycle>(raft.CycleId);
        var stale = Convert.ToBase64String(new byte[] { 0, 0, 0, 0, 0, 0, 0, 1 });

        var refused = await raft.Owner.Client.PostCloseAsync(raft.CycleId, CycleCloseTestKit.Assessment, stale);

        Assert.Equal((HttpStatusCode.Conflict, "MSG06"), (refused.StatusCode, await refused.ErrorCodeAsync()));
        Assert.Empty(await factory.CloseAuditAftersAsync(raft.CycleId));
    }

    private static string StateOf(System.Text.Json.JsonElement progress, int laneNumber) =>
        progress.GetProperty("lanes").EnumerateArray().Single(l => l.GetProperty("laneNumber").GetInt32() == laneNumber).Text("state");

    private Task<(int Sessions, int Logs, int Usages, int Lots, int Photos)> CountsAsync(Guid cycleId, Guid raftId) => factory.QueryAsync(async db => (
        await db.SeedingRecords.CountAsync(s => s.CycleId == cycleId),
        await db.CultivationLogs.CountAsync(l => l.CycleId == cycleId),
        await db.MaterialUsages.CountAsync(u => u.RaftId == raftId),
        await db.HarvestLots.CountAsync(l => l.CycleId == cycleId),
        await db.CultivationLogPhotos.CountAsync(p => p.Log.CycleId == cycleId)));
}
