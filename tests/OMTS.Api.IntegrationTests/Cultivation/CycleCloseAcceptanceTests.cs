using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using OMTS.Api.IntegrationTests.Farming;
using OMTS.Api.IntegrationTests.Harvest;
using OMTS.Domain.Constants;
using static OMTS.Api.IntegrationTests.Farming.FarmingTestKit;

namespace OMTS.Api.IntegrationTests.Cultivation;

/// <summary>AC-13 (CYCLE-06, HARVEST-01) on the days it names, the summary of BR-CYCLE-06 and the next cycle of BR-CYCLE-01. The clock is set by each test.</summary>
public class CycleCloseAcceptanceTests
{
    private static DateTimeOffset InVietnam(int month, int day, int hour = 9) => new(2026, month, day, hour, 0, 0, TimeSpan.FromHours(7));

    /// <summary>Zone KV01, raft KV01-B01 of ten lanes with a planned cycle from 01/10, and 100 kg of seed on every lane that day; the Zone Manager of the zone.</summary>
    private static async Task<(HttpClient Owner, Actor Manager, Guid ZoneId, Guid RaftId, Guid CycleId)> TenLanesAsync(CyclesQaClockApiFactory api)
    {
        var (owner, _) = await OwnerOfAsync(api);
        return await TenLanesAsync(api, owner);
    }

    private static async Task<(HttpClient Client, Guid AccountId)> OwnerOfAsync(CyclesQaClockApiFactory api)
    {
        var actor = await api.OwnerAsync();
        return (actor.Client, actor.Account.Id);
    }

    private static Task<JsonElement> CycleAsync(HttpClient client, Guid cycleId) => client.GetFromJsonAsync<JsonElement>($"/api/cultivation-cycles/{cycleId}");

    private static async Task<(HttpClient Owner, Actor Manager, Guid ZoneId, Guid RaftId, Guid CycleId)> TenLanesAsync(CyclesQaClockApiFactory api, HttpClient owner)
    {
        api.Clock.At = InVietnam(10, 1);
        var zoneId = (await owner.CreateZoneAsync(NewZoneBody("KV01"))).Id();
        var raftId = (await owner.CreateRaftAsync(zoneId, NewRaftBody("KV01-B01", lanes: 10))).Id();
        var cycleId = (await owner.StartCycleAsync(raftId, new CycleBody(new DateOnly(2026, 10, 1), 120m, new DateOnly(2026, 10, 30), 5000m))).Id();
        var lot = await owner.SeedLotAsync(received: new DateOnly(2026, 10, 1));
        await owner.RecordSeedingAsync(cycleId, lot.Session([1, 2, 3, 4, 5, 6, 7, 8, 9, 10], new DateOnly(2026, 10, 1), 100m));
        return (owner, await ManagerOfAsync(api, zoneId), zoneId, raftId, cycleId);
    }

    /// <summary>A Zone Manager of the zone, logged in on the real clock as the Owner is, so the token outlives the days the test moves to.</summary>
    private static async Task<Actor> ManagerOfAsync(CyclesQaClockApiFactory api, Guid zoneId)
    {
        var at = api.Clock.At;
        api.Clock.At = null;
        var manager = await api.ManagerAsync(zoneId);
        api.Clock.At = at;
        return manager;
    }

    private static HarvestFields Lanes1To9 => HarvestFields.OfLanes(1, 2, 3, 4, 5, 6, 7, 8, 9).FinishedLanes(1, 2, 3, 4, 5, 6, 7, 8, 9) with { Cages = "9" };

    [Fact]
    public async Task Ac_13_lane_10_growing_makes_it_an_early_close_that_goes_on_only_with_the_owners_approval()
    {
        using var api = new CyclesQaClockApiFactory();
        var (owner, manager, _, raftId, cycleId) = await TenLanesAsync(api);
        api.Clock.At = InVietnam(10, 26);
        await manager.Client.HarvestLotAsync(raftId, Lanes1To9);

        api.Clock.At = InVietnam(10, 28);
        var preview = await manager.Client.ClosePreviewAsync(cycleId);
        Assert.True(preview.Flag("isEarly"));
        Assert.True(preview.Flag("needsOwnerApproval"));
        Assert.Equal("MSG-CYCLE-07", preview.Text("earlyCloseMessageCode"));
        Assert.Equal(
            "This cycle is not finished: lanes 10 are not harvested. Oysters left on lanes that are Growing cannot be harvested in OMTS after the close. Closing it now needs the Cooperative Owner's approval. Has the Cooperative Owner approved closing it now?",
            preview.Text("earlyCloseMessage"));
        Assert.Equal(("MSG-CYCLE-05", "Close the cultivation cycle of raft KV01-B01? After closing, the cycle cannot be reopened and its records can no longer be changed."),
            (preview.Text("confirmMessageCode"), preview.Text("confirmMessage")));
        Assert.Equal([10], preview.GetProperty("summary").Numbers("notHarvestedLanes"));
        Assert.Equal("LanesNotHarvested", preview.GetProperty("summary").Text("earlyReason"));

        // Without that approval, nothing changes.
        var refused = await (await manager.Client.PostCloseAsync(cycleId, CycleCloseTestKit.Assessment, preview.Text("cycleRowVersion"))).ReadAsync(HttpStatusCode.Conflict);
        Assert.Equal(("MSG-CYCLE-07", "ownerApproved", "LanesNotHarvested"), (refused.Text("code"), refused.Text("confirmFlag"), refused.Text("earlyReason")));
        Assert.Equal([10], refused.Numbers("laneNumbers"));
        Assert.Equal(StatusIds.CultivationCycle.Open, (await CycleAsync(owner, cycleId)).GetProperty("statusId").GetInt32());
        Assert.Empty(await api.CloseAuditAftersAsync(cycleId));
        Assert.Empty(await api.ClosedNoticesAsync(cycleId));

        // With it, the cycle closes early today and lane 10 ends with no harvest weight.
        var closed = await manager.Client.CloseCycleAsync(cycleId, ownerApproved: true);
        var cycle = closed.GetProperty("cycle");
        Assert.Equal((StatusIds.CultivationCycle.Closed, new DateOnly(2026, 10, 28), true, CycleCloseTestKit.Assessment),
            (cycle.GetProperty("statusId").GetInt32(), cycle.Date("closeDate"), cycle.Flag("closedEarly"), cycle.Text("assessment")));
        var summary = closed.GetProperty("summary");
        Assert.Equal([10], summary.Numbers("notHarvestedLanes"));
        Assert.Equal((450m, 100m, "kg", 26), (summary.Number("kgHarvested"), summary.Number("seedHung"), summary.Text("seedUnit"), summary.GetProperty("daysOfFarming").GetInt32()));
        var atClose = await owner.ProgressAsync(raftId, $"?cycleId={cycleId}");
        Assert.Equal("Growing", atClose.GetProperty("lanes").EnumerateArray().Single(l => l.GetProperty("laneNumber").GetInt32() == 10).Text("state"));
        Assert.Contains("\"OwnerApprovalConfirmed\":true", Assert.Single(await api.CloseAuditAftersAsync(cycleId)));

        // Its oysters can no longer be harvested in OMTS: a harvest belongs to the Open cycle of its date.
        Assert.Equal("MSG-GEN-09", (await (await manager.Client.PostHarvestAsync(raftId, HarvestFields.OfLanes(10))).ReadAsync(HttpStatusCode.Conflict)).Text("code"));
    }

    [Fact]
    public async Task Ac_13_lane_10_marked_finished_reminds_the_zone_managers_and_the_cycle_closes_without_approval()
    {
        using var api = new CyclesQaClockApiFactory();
        var (owner, ownerId) = await OwnerOfAsync(api);
        var (_, manager, _, raftId, cycleId) = await TenLanesAsync(api, owner);
        api.Clock.At = InVietnam(10, 26);
        await manager.Client.HarvestLotAsync(raftId, Lanes1To9);
        api.Clock.At = InVietnam(10, 27);
        await manager.Client.HarvestLotAsync(raftId, HarvestFields.OfLanes(10).FinishedLanes(10) with { Cages = "1", EstimatedKg = "45.5" });

        Assert.Contains(await api.NoticesAsync("CYCLE_CLOSE_REMINDER", cycleId), n => n.RecipientAccountId == manager.Account.Id);
        var preview = await manager.Client.ClosePreviewAsync(cycleId);
        Assert.False(preview.Flag("isEarly"));
        Assert.False(preview.Flag("needsOwnerApproval"));
        Assert.Equal(JsonValueKind.Null, preview.GetProperty("earlyCloseMessage").ValueKind);

        api.Clock.At = InVietnam(10, 29);
        var closed = await manager.Client.CloseCycleAsync(cycleId);

        Assert.False(closed.GetProperty("cycle").Flag("closedEarly"));
        var summary = closed.GetProperty("summary");
        Assert.Empty(summary.Numbers("notHarvestedLanes"));
        Assert.Equal(JsonValueKind.Null, summary.GetProperty("earlyReason").ValueKind);
        // BR-CYCLE-06: the days run to the last harvest lot, both days counted; the plan stands next to the actual figures.
        Assert.Equal((495.5m, 27, new DateOnly(2026, 10, 27), 2), (summary.Number("kgHarvested"), summary.GetProperty("daysOfFarming").GetInt32(), summary.Date("lastHarvestDate"), summary.GetProperty("harvestLotCount").GetInt32()));
        Assert.Equal((120m, 5000m), (summary.Number("expectedSeedQuantity"), summary.Number("targetYieldKg")));
        Assert.False(summary.TryGetProperty("expectedHarvestDate", out _)); // the plan has no expected harvest date any more (owner decision of 12/11/2026)
        Assert.Contains("\"OwnerApprovalConfirmed\":false", Assert.Single(await api.CloseAuditAftersAsync(cycleId)));

        // MSG-CYCLE-06 to the Owner with the figures of the close day.
        var notice = Assert.Single(await api.ClosedNoticesAsync(cycleId));
        Assert.Equal((ownerId, "MSG-CYCLE-06", false, "CultivationCycle", "KV01-B01"),
            (notice.RecipientAccountId, notice.MessageCode, notice.IsMandatory, notice.RelatedRecordType, notice.RelatedRecordCode));
        Assert.Equal("Raft KV01-B01: the cultivation cycle was closed on 29/10/2026. Seed hung: 100 kg. Harvested: about 495.5 kg. Days of farming: 27. Please review the summary.", notice.Body);
        using var parameters = JsonDocument.Parse(notice.MessageParams!);
        Assert.Equal(("2026-10-29", false, 495.5m, 27), (parameters.RootElement.Text("close_date"), parameters.RootElement.GetProperty("closed_early").GetBoolean(),
            parameters.RootElement.GetProperty("kg_harvested").GetDecimal(), parameters.RootElement.GetProperty("days").GetInt32()));
    }

    [Fact]
    public async Task The_owner_closes_early_with_a_confirmation_only_and_is_not_told_of_their_own_close()
    {
        using var api = new CyclesQaClockApiFactory();
        var (owner, _, zoneId, raftId, cycleId) = await TenLanesAsync(api);

        var preview = await owner.ClosePreviewAsync(cycleId);
        Assert.True(preview.Flag("isEarly"));
        Assert.False(preview.Flag("needsOwnerApproval"));
        Assert.True((await owner.ZoneRowAsync(zoneId, raftId)).Flag("canClose"));

        var closed = await owner.CloseCycleAsync(cycleId);

        Assert.True(closed.GetProperty("cycle").Flag("closedEarly"));
        Assert.Equal([1, 2, 3, 4, 5, 6, 7, 8, 9, 10], closed.GetProperty("summary").Numbers("notHarvestedLanes"));
        Assert.Equal((0m, 0, 1), (closed.GetProperty("summary").Number("kgHarvested"), closed.GetProperty("summary").GetProperty("harvestLotCount").GetInt32(), closed.GetProperty("summary").GetProperty("daysOfFarming").GetInt32()));
        Assert.Contains("\"OwnerApprovalConfirmed\":false", Assert.Single(await api.CloseAuditAftersAsync(cycleId)));
        Assert.Empty(await api.ClosedNoticesAsync(cycleId));
        var row = await owner.ZoneRowAsync(zoneId, raftId);
        Assert.Equal(("Closed", true, false, false), (row.GetProperty("cycle").Text("status"), row.GetProperty("cycle").Flag("closedEarly"), row.Flag("canEdit"), row.Flag("canClose")));
    }

    [Fact]
    public async Task A_cycle_with_no_seeding_session_is_an_early_close_that_names_the_reason()
    {
        using var api = new CyclesQaClockApiFactory();
        var owner = (await api.OwnerAsync()).Client;
        api.Clock.At = InVietnam(10, 1);
        var zoneId = (await owner.CreateZoneAsync()).Id();
        var raftId = (await owner.CreateRaftAsync(zoneId)).Id();
        var cycleId = (await owner.StartCycleAsync(raftId)).Id();
        var manager = await ManagerOfAsync(api, zoneId);
        api.Clock.At = InVietnam(10, 5);

        var preview = await manager.Client.ClosePreviewAsync(cycleId);
        Assert.StartsWith("This cycle is not finished: no seeding session is recorded.", preview.Text("earlyCloseMessage"));
        Assert.Equal("NoSeedingSession", preview.GetProperty("summary").Text("earlyReason"));
        Assert.Equal(JsonValueKind.Null, preview.GetProperty("summary").GetProperty("seedUnit").ValueKind);

        var closed = await manager.Client.CloseCycleAsync(cycleId, ownerApproved: true);

        // No harvest: the days run to the close date.
        Assert.Equal(5, closed.GetProperty("summary").GetProperty("daysOfFarming").GetInt32());
        var notice = Assert.Single(await api.ClosedNoticesAsync(cycleId));
        Assert.EndsWith("closed on 05/10/2026 early. Seed hung: 0. Harvested: about 0 kg. Days of farming: 5. Please review the summary.", notice.Body);
    }

    [Fact]
    public async Task The_next_cycle_starts_from_the_day_after_the_close_date()
    {
        using var api = new CyclesQaClockApiFactory();
        var owner = (await api.OwnerAsync()).Client;
        api.Clock.At = InVietnam(10, 1);
        var zoneId = (await owner.CreateZoneAsync()).Id();
        var raftId = (await owner.CreateRaftAsync(zoneId)).Id();
        var cycleId = (await owner.StartCycleAsync(raftId)).Id();
        api.Clock.At = InVietnam(10, 20);
        await owner.CloseCycleAsync(cycleId);

        Assert.False((await owner.ZoneRowAsync(zoneId, raftId)).Flag("canStart"));
        var today = await (await owner.PostCycleAsync(raftId)).ReadAsync(HttpStatusCode.BadRequest);
        Assert.Equal(("MSG-GEN-01", "Start date"), (today.Text("code"), today.Text("field")));

        api.Clock.At = InVietnam(10, 21);
        Assert.True((await owner.ZoneRowAsync(zoneId, raftId)).Flag("canStart"));
        Assert.Equal(("MSG-GEN-01", "Start date"), await StartRefusalAsync(owner, raftId, new DateOnly(2026, 10, 20)));
        var next = await owner.StartCycleAsync(raftId);
        Assert.Equal(new DateOnly(2026, 10, 21), next.Date("startDate"));

        // The closed cycle keeps its close day; the list shows both, the newest first.
        var list = (await owner.GetFromJsonAsync<JsonElement>($"/api/rafts/{raftId}/cultivation-cycles")).EnumerateArray().ToList();
        Assert.Equal([next.Id(), cycleId], list.Select(c => c.Id()));
        Assert.Equal((new DateOnly(2026, 10, 20), true), (list[1].Date("closeDate"), list[1].Flag("closedEarly")));
    }

    private static async Task<(string, string)> StartRefusalAsync(HttpClient owner, Guid raftId, DateOnly start)
    {
        var refused = await (await owner.PostCycleAsync(raftId, new CycleBody(start))).ReadAsync(HttpStatusCode.BadRequest);
        return (refused.Text("code"), refused.Text("field"));
    }
}
