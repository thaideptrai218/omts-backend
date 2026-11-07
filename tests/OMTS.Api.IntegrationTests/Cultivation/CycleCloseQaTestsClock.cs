using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using OMTS.Api.IntegrationTests.Farming;
using OMTS.Api.IntegrationTests.Harvest;
using OMTS.Api.IntegrationTests.Infrastructure;
using OMTS.Infrastructure.Messaging.Fakes;
using static OMTS.Api.IntegrationTests.Farming.FarmingTestKit;

namespace OMTS.Api.IntegrationTests.Cultivation;

/// <summary>
/// QA of PR C5 on the days the SRS names, with a clock each test moves (one fresh API per test, so the cooperative has one Owner): the business day of the close
/// (BR-CYCLE-07), the closed summary over time and next to the next cycle (BR-CYCLE-06, BR-CYCLE-01), the MSG-CYCLE-06 notice and its push (CYCLE-06 step 6,
/// BR-ACCOUNT-11) and the audit row of the close (GBR-02).
/// </summary>
public class CycleCloseQaTestsClock
{
    private static DateTimeOffset InVietnam(int month, int day, int hour = 9, int minute = 0) => new(2026, month, day, hour, minute, 0, TimeSpan.FromHours(7));

    private static DateOnly On(int month, int day) => new(2026, month, day);

    private sealed record Setup(Actor Owner, Actor Manager, Guid ZoneId, Guid RaftId, string RaftCode, Guid CycleId);

    /// <summary>A raft of five lanes with a cycle from 01/10 and one 50 kg session on lanes 1 to 5 that day; Owner and Zone Manager log in on the real clock.</summary>
    private static async Task<Setup> ArrangeAsync(CyclesQaClockApiFactory api)
    {
        var owner = await api.OwnerAsync();
        api.Clock.At = InVietnam(10, 1);
        var zoneId = (await owner.Client.CreateZoneAsync()).Id();
        var raft = await owner.Client.CreateRaftAsync(zoneId);
        var cycleId = (await owner.Client.StartCycleAsync(raft.Id(), new CycleBody(On(10, 1)))).Id();
        var lot = await owner.Client.SeedLotAsync(received: On(10, 1));
        await owner.Client.RecordSeedingAsync(cycleId, lot.Session([1, 2, 3, 4, 5], On(10, 1), 50m));
        api.Clock.At = null;
        var manager = await api.ManagerAsync(zoneId);
        api.Clock.At = InVietnam(10, 1);
        return new Setup(owner, manager, zoneId, raft.Id(), raft.Text("code"), cycleId);
    }

    private static List<FakeMessage> ClosedPushes(CyclesQaClockApiFactory api, Guid accountId) =>
        api.Services.GetRequiredService<FakePushSender>().Inbox.List(accountId.ToString()).Where(m => m.MessageCode == "MSG-CYCLE-06").ToList();

    // BR-CYCLE-07: the close date is the day of the close in Vietnam; 00:30 there is still 17:30 UTC of the day before.
    [Fact]
    public async Task A_close_half_an_hour_after_midnight_in_vietnam_is_dated_that_day()
    {
        using var api = new CyclesQaClockApiFactory();
        var s = await ArrangeAsync(api);

        api.Clock.At = InVietnam(10, 21, 0, 30);
        var closed = await s.Owner.Client.CloseCycleAsync(s.CycleId);

        Assert.Equal(On(10, 21), closed.GetProperty("cycle").Date("closeDate"));
        // No harvest: the days run to the close date, both counted.
        Assert.Equal(21, closed.GetProperty("summary").GetProperty("daysOfFarming").GetInt32());

        // CYCLE-05.1: a date inside the closed cycle shows it with its summary; a date after the close, with no cycle Open, shows every lane Not seeded.
        api.Clock.At = InVietnam(10, 25);
        var inside = await s.Owner.Client.ProgressAsync(s.RaftId, "?date=2026-10-05");
        Assert.Equal(On(10, 21), inside.GetProperty("summary").Date("closeDate"));
        var after = await s.Owner.Client.ProgressAsync(s.RaftId, "?date=2026-10-23");
        Assert.True(after.IsNull("cycle") && after.IsNull("summary"), after.GetRawText());
        Assert.All(after.GetProperty("lanes").EnumerateArray(), l => Assert.Equal("NotSeeded", l.Text("state")));
    }

    // BR-CYCLE-06 / CYCLE-05.1: the summary of a closed cycle is read as of its close date: time going on and the next cycle of the raft change nothing in it.
    [Fact]
    public async Task The_closed_summary_does_not_move_with_time_or_with_the_next_cycle_of_the_raft()
    {
        using var api = new CyclesQaClockApiFactory();
        var s = await ArrangeAsync(api);
        var owner = s.Owner.Client;
        api.Clock.At = InVietnam(10, 10);
        await owner.HarvestLotAsync(s.RaftId, HarvestFields.OfLanes(1, 2).FinishedLanes(1, 2) with { EstimatedKg = "300" });
        api.Clock.At = InVietnam(10, 12);
        var atClose = (await owner.CloseCycleAsync(s.CycleId)).GetProperty("summary");
        Assert.Equal((10, On(10, 10), 300m, 50m), (atClose.GetProperty("daysOfFarming").GetInt32(), atClose.Date("lastHarvestDate"), atClose.Number("kgHarvested"), atClose.Number("seedHung")));
        Assert.Equal([3, 4, 5], atClose.Numbers("notHarvestedLanes"));
        api.Clock.At = InVietnam(10, 13);
        var next = (await owner.StartCycleAsync(s.RaftId, new CycleBody(On(10, 13)))).Id();
        var lot = await owner.SeedLotAsync(received: On(10, 13));
        await owner.RecordSeedingAsync(next, lot.Session([1, 2, 3, 4, 5], On(10, 13), 70m));
        api.Clock.At = InVietnam(10, 25);
        await owner.HarvestLotAsync(s.RaftId, HarvestFields.OfLanes(1, 2, 3, 4, 5).FinishedLanes(1, 2, 3, 4, 5) with { EstimatedKg = "999" });
        api.Clock.At = InVietnam(11, 5);

        Assert.Equal(atClose.GetRawText(), (await owner.CycleSummaryAsync(s.CycleId)).GetRawText());
        var old = await owner.ProgressAsync(s.RaftId, $"?cycleId={s.CycleId}");
        Assert.Equal(On(10, 12), old.Date("date"));
        Assert.Equal(atClose.GetRawText(), old.GetProperty("summary").GetRawText());
        Assert.Equal(["Harvested", "Harvested", "Growing", "Growing", "Growing"],
            old.GetProperty("lanes").EnumerateArray().OrderBy(l => l.GetProperty("laneNumber").GetInt32()).Select(l => l.Text("state")));
        Assert.Equal(10, (await owner.GetFromJsonAsync<JsonElement>($"/api/cultivation-cycles/{s.CycleId}")).GetProperty("daysOfFarming").GetInt32());

        // The next cycle has its own figures; usage on the close day stays locked, the first day of the next cycle is free.
        var current = await owner.CycleSummaryAsync(next);
        Assert.Equal((999m, 70m, false), (current.Number("kgHarvested"), current.Number("seedHung"), current.Flag("closedEarly")));
        // BR-CYCLE-01: the next cycle's start date cannot be corrected back onto the close day of the closed one (no overlap).
        var nextView = await owner.GetFromJsonAsync<JsonElement>($"/api/cultivation-cycles/{next}");
        var overlap = await (await owner.PutCycleAsync(next, new CycleBody(On(10, 12), RowVersion: nextView.Text("rowVersion")))).ReadAsync(HttpStatusCode.BadRequest);
        Assert.Equal(("MSG-GEN-01", "Start date"), (overlap.Text("code"), overlap.Text("field")));
        var item = await api.ItemAsync();
        Assert.Equal("MSG-GEN-09", await (await owner.PostUsageAsync(s.RaftId, new UsageBody(On(10, 12), MaterialUsageTestKit.BrokenBuoy, [new UsageLine(item)]))).ErrorCodeAsync());
        Assert.Equal(HttpStatusCode.Created, (await owner.PostUsageAsync(s.RaftId, new UsageBody(On(10, 13), MaterialUsageTestKit.BrokenBuoy, [new UsageLine(item)]))).StatusCode);
    }

    // CYCLE-06 step 6 and MSG-CYCLE-06: one notice to the Owner with the early note naming the lanes, pushed once (push Default, BR-ACCOUNT-11); the Zone Manager gets none;
    // the notice opens the closed cycle in its zone (CYCLE-05.2).
    [Fact]
    public async Task A_zone_managers_early_close_tells_and_pushes_the_owner_once_and_the_notice_opens_the_cycle()
    {
        using var api = new CyclesQaClockApiFactory();
        var s = await ArrangeAsync(api);
        api.Clock.At = InVietnam(10, 10);
        await s.Manager.Client.HarvestLotAsync(s.RaftId, HarvestFields.OfLanes(1, 2).FinishedLanes(1, 2) with { EstimatedKg = "300" });
        api.Clock.At = InVietnam(10, 12);

        await s.Manager.Client.CloseCycleAsync(s.CycleId, ownerApproved: true);

        var notice = Assert.Single(await api.ClosedNoticesAsync(s.CycleId));
        Assert.Equal(s.Owner.Account.Id, notice.RecipientAccountId);
        Assert.Equal(
            $"Raft {s.RaftCode}: the cultivation cycle was closed on 12/10/2026 early; lanes not harvested: 3, 4, 5. Seed hung: 50 kg. Harvested: about 300 kg. Days of farming: 10. Please review the summary.",
            notice.Body);
        Assert.Equal(notice.Id, Assert.Single(ClosedPushes(api, s.Owner.Account.Id)).NotificationId);
        Assert.Empty(ClosedPushes(api, s.Manager.Account.Id));

        var opened = await s.Owner.Client.GetFromJsonAsync<JsonElement>($"/api/me/notifications/{notice.Id}/open");
        Assert.Equal(("CultivationCycle", s.CycleId, s.ZoneId), (opened.Text("recordType"), opened.GetProperty("recordId").GetGuid(), opened.GetProperty("zoneId").GetGuid()));
    }

    // BR-ACCOUNT-11/12: CYCLE_CLOSED is not mandatory; an Owner who turned its push off still finds the notice in the list, without a push.
    [Fact]
    public async Task An_owner_who_turned_the_push_off_keeps_the_notice_without_a_push()
    {
        using var api = new CyclesQaClockApiFactory();
        var s = await ArrangeAsync(api);
        var off = await s.Owner.Client.PutAsJsonAsync("/api/me/notification-preferences", new { items = new[] { new { type = CycleCloseTestKit.ClosedType, push = false } } });
        Assert.True(off.IsSuccessStatusCode, await off.Content.ReadAsStringAsync());

        await s.Manager.Client.CloseCycleAsync(s.CycleId, ownerApproved: true);

        Assert.Single(await api.ClosedNoticesAsync(s.CycleId));
        Assert.Empty(ClosedPushes(api, s.Owner.Account.Id));
    }

    // GBR-02: a close is written to the audit log with the person, and the values BEFORE and after. The repo writes Before/After with the status for every other
    // close (e.g. HarvestLot.Closed); CultivationCycle.Closed has no Before at all and no status in After.
    // Expected to FAIL until fixed: CycleCloseService.CloseAsync records After only.
    [Fact]
    public async Task The_audit_row_of_the_close_holds_the_values_before_and_after_with_the_person()
    {
        using var api = new CyclesQaClockApiFactory();
        var s = await ArrangeAsync(api);
        api.Clock.At = InVietnam(10, 12);

        await s.Manager.Client.CloseCycleAsync(s.CycleId, ownerApproved: true);

        var row = await api.QueryAsync(db => db.AuditEvents.Where(e => e.Action == CycleCloseTestKit.ClosedAction && e.RecordId == s.CycleId)
            .Select(e => new { e.ActorAccountId, e.ZoneId, e.Before, e.After }).SingleAsync());
        Assert.Equal((s.Manager.Account.Id, s.ZoneId), (row.ActorAccountId, row.ZoneId));
        Assert.False(string.IsNullOrEmpty(row.Before), "GBR-02: the close must keep the values before (status Open, no close date, no assessment).");
        Assert.Contains("\"Status\":\"CLOSED\"", row.After!, StringComparison.OrdinalIgnoreCase);
    }
}
