using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using OMTS.Api.IntegrationTests.Cultivation;
using OMTS.Api.IntegrationTests.Farming;
using OMTS.Api.IntegrationTests.Infrastructure;
using OMTS.Domain.Constants;
using OMTS.Domain.Entities.Cultivation;
using OMTS.Domain.Entities.Farming;
using OMTS.Domain.Entities.Identity;
using OMTS.Domain.Enums;
using OMTS.Infrastructure.Messaging.Fakes;
using static OMTS.Api.IntegrationTests.Farming.FarmingTestKit;
using static OMTS.Api.IntegrationTests.Infrastructure.AcsTestKit;

namespace OMTS.Api.IntegrationTests.Harvest;

/// <summary>QA of PR H1a: AC-13 step by step, mixed and revoked pairs, the fields a phone may send, lane states on the views around them.</summary>
public class HarvestLotsQaTests(AuthApiFactory factory) : IClassFixture<AuthApiFactory>
{
    private const string ReminderType = "CYCLE_CLOSE_REMINDER";

    private static async Task AssertForbiddenAsync(HttpResponseMessage answer)
    {
        Assert.Equal(HttpStatusCode.Forbidden, answer.StatusCode);
        Assert.Equal("MSG-GEN-08", await answer.ErrorCodeAsync());
    }

    private static async Task AssertRefusedEverywhereAsync(HttpClient client, Guid raftId, Guid lotId)
    {
        await AssertForbiddenAsync(await client.PostHarvestAsync(raftId));
        await AssertForbiddenAsync(await client.GetAsync($"/api/rafts/{raftId}/harvest-lots"));
        await AssertForbiddenAsync(await client.GetAsync($"/api/harvest-lots/{lotId}"));
        await AssertForbiddenAsync(await client.GetAsync($"/api/rafts/{raftId}/lanes/1/progress"));
    }

    private Task RevokeAsync(Guid grantId) =>
        factory.UpdateAsync<AccessGrant>(grantId, g => { g.StatusId = StatusIds.AccessGrant.Revoked; g.RevokedAt = DateTimeOffset.UtcNow; });

    // AC-13 as it reads, on ten lanes: lanes 1 to 9 Harvested and lane 10 Growing send nothing; lane 10 in a lot but not finished sends nothing; lane 10 finished reminds the Zone Managers of the zone only.
    [Fact]
    public async Task Ac_13_the_reminder_comes_when_lane_10_of_ten_is_finished_and_only_to_the_zone_managers_of_the_zone()
    {
        // Its own host: an All zones manager of another test would be told too.
        using var factory = new AuthApiFactory();
        var owner = await factory.OwnerAsync();
        var zoneId = (await owner.Client.CreateZoneAsync()).Id();
        var raftId = (await owner.Client.CreateRaftAsync(zoneId, NewRaftBody(lanes: 10))).Id();
        var cycleId = (await owner.Client.StartCycleAsync(raftId, new CycleBody(Today.AddDays(-10)))).Id();
        var seedLot = await owner.Client.SeedLotAsync();
        await owner.Client.SeedingRecordAsync(cycleId, seedLot.Session(Enumerable.Range(1, 10).ToList()));
        var (manager, allZones) = (await factory.ManagerAsync(zoneId), await factory.StaffAsync(null));
        var elsewhere = await factory.ManagerAsync((await owner.Client.CreateZoneAsync()).Id());
        var accountant = await factory.StaffAsync(null, StaffRole.Accountant);
        var nine = Enumerable.Range(1, 9).ToArray();

        var first = await manager.Client.RecordHarvestAsync(raftId, HarvestFields.OfLanes(nine).FinishedLanes(nine));

        Assert.False(first.Flag("everySeededLaneHarvested"));
        Assert.Empty(await factory.NoticesAsync(ReminderType, cycleId));
        var screen = await manager.Client.HarvestScreenAsync(raftId);
        Assert.All(nine, lane => Assert.Equal("Harvested", screen.HarvestStateOf(lane)));
        Assert.Equal("Growing", screen.HarvestStateOf(10));

        var notFinished = await manager.Client.RecordHarvestAsync(raftId, HarvestFields.OfLanes(10));

        Assert.False(notFinished.Flag("everySeededLaneHarvested"));
        Assert.Empty(await factory.NoticesAsync(ReminderType, cycleId));
        Assert.Equal("BeingHarvested", (await manager.Client.HarvestScreenAsync(raftId)).HarvestStateOf(10));

        var last = await manager.Client.RecordHarvestAsync(raftId, HarvestFields.OfLanes(10).FinishedLanes(10));

        Assert.True(last.Flag("everySeededLaneHarvested"));
        var rows = await factory.NoticesAsync(ReminderType, cycleId);
        Assert.Equal(new[] { manager.Account.Id, allZones.Account.Id }.Order(), rows.Select(n => n.RecipientAccountId!.Value).Order());
        Assert.All(rows, n => Assert.Equal(("MSG-CYCLE-08", true), (n.MessageCode, n.IsMandatory)));
        Assert.DoesNotContain(rows, n => n.RecipientAccountId == elsewhere.Account.Id || n.RecipientAccountId == accountant.Account.Id || n.RecipientAccountId == owner.Account.Id);
        var pushes = factory.Services.GetRequiredService<FakePushSender>().Inbox.List(manager.Account.Id.ToString());
        Assert.Contains(pushes, m => m.Subject == "Reminder to close a cultivation cycle");
        Assert.False((await manager.Client.HarvestScreenAsync(raftId)).Flag("canRecordHarvest"));
    }

    [Fact]
    public async Task A_manager_elsewhere_with_accountant_pairs_here_and_on_all_zones_is_refused_on_every_route_and_a_manager_here_with_an_all_zones_accountant_pair_is_not()
    {
        var raft = await factory.SeededRaftAsync();
        var lot = await raft.Owner.Client.HarvestLotAsync(raft.RaftId);
        var here = await factory.ZoneEntityAsync(raft.ZoneId);
        var elsewhere = await factory.ZoneEntityAsync((await raft.Owner.Client.CreateZoneAsync()).Id());

        var (mixed, _) = await factory.StaffAccountAsync(elsewhere);
        await factory.GrantAsync(mixed, StaffRole.Accountant, here);
        await factory.GrantAsync(mixed, StaffRole.Accountant, null);
        await AssertRefusedEverywhereAsync(await factory.LoginAsync(mixed.Username), raft.RaftId, lot.Id());

        var (both, _) = await factory.StaffAccountAsync(here);
        await factory.GrantAsync(both, StaffRole.Accountant, null);
        var client = await factory.LoginAsync(both.Username);
        Assert.Equal(lot.Id(), (await client.GetHarvestLotAsync(lot.Id())).Id());
        Assert.Equal(HttpStatusCode.Created, (await client.PostHarvestAsync(raft.RaftId)).StatusCode);
    }

    [Fact]
    public async Task A_revoked_pair_refuses_the_next_action_on_every_route_stops_the_reminder_and_a_new_pair_gives_the_lots_back()
    {
        var raft = await factory.SeededRaftAsync(1);
        var manager = await factory.ManagerAsync(raft.ZoneId);
        var colleague = await factory.ManagerAsync(raft.ZoneId);
        var lot = await manager.Client.HarvestLotAsync(raft.RaftId);

        await RevokeAsync(manager.Grant!.Id);

        await AssertRefusedEverywhereAsync(manager.Client, raft.RaftId, lot.Id());
        Assert.Equal(manager.Account.DisplayName, (await raft.Owner.Client.GetHarvestLotAsync(lot.Id())).Text("recordedByName"));

        await colleague.Client.HarvestLotAsync(raft.RaftId, HarvestFields.OfLanes(1).FinishedLanes(1));
        var told = (await factory.NoticesAsync(ReminderType, raft.CycleId)).Select(n => n.RecipientAccountId!.Value).ToList();
        Assert.Contains(colleague.Account.Id, told);
        Assert.DoesNotContain(manager.Account.Id, told);

        await factory.GrantAsync(manager.Account, StaffRole.ZoneManager, await factory.ZoneEntityAsync(raft.ZoneId));
        Assert.Equal(lot.Id(), (await manager.Client.GetHarvestLotAsync(lot.Id())).Id());
    }

    // GBR-01, BR-FARM-08: the pairs of a Discontinued zone stay, for its history; nothing new is recorded.
    [Fact]
    public async Task In_a_discontinued_zone_the_manager_still_reads_the_lots_and_cannot_record()
    {
        var raft = await factory.SeededRaftAsync();
        var manager = await factory.ManagerAsync(raft.ZoneId);
        var lot = await manager.Client.HarvestLotAsync(raft.RaftId);

        await factory.UpdateAsync<FarmingZone>(raft.ZoneId, z => z.StatusId = StatusIds.Usage.Discontinued);

        var screen = await manager.Client.HarvestScreenAsync(raft.RaftId);
        Assert.False(screen.Flag("canRecordHarvest"));
        Assert.All(screen.GetProperty("lanes").EnumerateArray(), l => Assert.False(l.Flag("canHarvest")));
        Assert.Single(screen.LotCodes());
        Assert.Equal(lot.Id(), (await manager.Client.GetHarvestLotAsync(lot.Id())).Id());
        Assert.Equal("MSG-GEN-09", await (await manager.Client.PostHarvestAsync(raft.RaftId)).ErrorCodeAsync());
    }

    // The fields a phone or a crafted client may send: 4xx with nothing saved, never a 500.
    public static TheoryData<string, HttpContent> BrokenBodies()
    {
        static HttpContent Raw(string text, string contentType)
        {
            var content = new ByteArrayContent(Encoding.UTF8.GetBytes(text));
            content.Headers.ContentType = MediaTypeHeaderValue.Parse(contentType);
            return content;
        }

        var manyLanes = new MultipartFormDataContent();
        for (var i = 0; i < 1100; i++)
        {
            manyLanes.Add(new StringContent("1"), "laneNumbers");
        }

        return new TheoryData<string, HttpContent>
        {
            { "no boundary", Raw("--x\r\n", "multipart/form-data") },
            { "junk", Raw("not multipart at all", "multipart/form-data; boundary=x") },
            { "cut off", Raw("--x\r\nContent-Disposition: form-data; name=\"cageCount\"\r\n\r\n8", "multipart/form-data; boundary=x") },
            { "empty", new MultipartFormDataContent() },
            { "over the field limit", manyLanes },
        };
    }

    [Theory]
    [MemberData(nameof(BrokenBodies))]
    public async Task A_broken_multipart_body_answers_4xx_and_saves_nothing(string name, HttpContent body)
    {
        var raft = await factory.SeededRaftAsync();

        var answer = await raft.Owner.Client.PostAsync($"/api/rafts/{raft.RaftId}/harvest-lots", body);

        Assert.True((int)answer.StatusCode is >= 400 and < 500, $"{name}: {(int)answer.StatusCode} {await answer.Content.ReadAsStringAsync()}");
        Assert.Equal(0, await factory.LotCountAsync(raft.RaftId));
    }

    [Theory]
    [InlineData("8.0")]
    [InlineData("8,5")]
    [InlineData("1e3")]
    [InlineData("0x10")]
    [InlineData("١٢")]
    public async Task Cages_are_a_plain_whole_number(string cages)
    {
        var raft = await factory.SeededRaftAsync();

        var answer = await (await raft.Owner.Client.PostHarvestAsync(raft.RaftId, new HarvestFields { Cages = cages })).ReadAsync(HttpStatusCode.BadRequest);

        Assert.Equal("Number of cages", answer.Text("field"));
    }

    [Theory]
    [InlineData("1e3")]
    [InlineData("0x10")]
    [InlineData("NaN")]
    [InlineData("Infinity")]
    [InlineData("79228162514264337593543950336")]
    public async Task An_estimate_that_is_no_plain_number_is_refused(string estimate)
    {
        var raft = await factory.SeededRaftAsync();

        var answer = await (await raft.Owner.Client.PostHarvestAsync(raft.RaftId, new HarvestFields { EstimatedKg = estimate })).ReadAsync(HttpStatusCode.BadRequest);

        Assert.Equal("Estimated kg", answer.Text("field"));
        Assert.Equal(0, await factory.LotCountAsync(raft.RaftId));
    }

    // A Vietnamese phone writes 400,5 for four hundred and a half. The client list says an unreadable estimate is refused; it must not be read as 4005.
    [Theory]
    [InlineData("400,5")]
    [InlineData("12,50")]
    public async Task L1_an_estimate_with_a_decimal_comma_is_refused_not_read_ten_times_larger(string estimate)
    {
        var raft = await factory.SeededRaftAsync();

        var answer = await raft.Owner.Client.PostHarvestAsync(raft.RaftId, new HarvestFields { EstimatedKg = estimate });

        var text = await answer.Content.ReadAsStringAsync();
        Assert.True(answer.StatusCode == HttpStatusCode.BadRequest, $"{estimate} was answered {(int)answer.StatusCode}: {text}");
        Assert.Equal(0, await factory.LotCountAsync(raft.RaftId));
    }

    // BR-HARVEST-02: the estimate is cages x the copy; decimals of the setting survive, nothing rounds, and the limits hold for what is worked out.
    [Theory]
    [InlineData(33.33, "3", 99.99)]
    [InlineData(0.01, "1", 0.01)]
    [InlineData(12.35, "7", 86.45)]
    [InlineData(50, "20000000", 1000000000)]
    public async Task The_estimate_is_worked_out_exactly_from_the_setting(decimal average, string cages, decimal estimate)
    {
        var raft = await factory.SeededRaftAsync();
        await raft.Owner.Client.SetHarvestSettingsAsync(average, 3);

        var lot = await raft.Owner.Client.HarvestLotAsync(raft.RaftId, new HarvestFields { Cages = cages });

        Assert.Equal((average, estimate), (lot.Kg("kgPerCage"), lot.Kg("estimatedKg")));
        await raft.Owner.Client.SetHarvestSettingsAsync(50m, 3);
    }

    [Fact]
    public async Task A_worked_out_estimate_above_the_limit_is_refused_and_a_sent_one_decides_for_a_huge_count()
    {
        var raft = await factory.SeededRaftAsync();
        var owner = raft.Owner.Client;

        var refused = await (await owner.PostHarvestAsync(raft.RaftId, new HarvestFields { Cages = "20000001" })).ReadAsync(HttpStatusCode.BadRequest);
        Assert.Equal("Estimated kg", refused.Text("field"));

        var lot = await owner.HarvestLotAsync(raft.RaftId, new HarvestFields { Cages = "2147483647", EstimatedKg = "1" });
        Assert.Equal((2147483647, 1m), (lot.GetProperty("cageCount").GetInt32(), lot.Kg("estimatedKg")));
    }

    // The preview is a query of a whole number of days: whatever else comes answers MSG-GEN-01.
    [Theory]
    [InlineData("abc")]
    [InlineData("1.5")]
    [InlineData("99999999999")]
    [InlineData("")]
    public async Task The_preview_answers_msg_gen_01_on_days_it_cannot_read(string days)
    {
        var owner = await factory.OwnerAsync();

        var answer = await owner.Client.GetAsync($"/api/harvest-settings/auto-close-preview?days={days}");

        Assert.Equal(HttpStatusCode.BadRequest, answer.StatusCode);
        Assert.Equal("MSG-GEN-01", await answer.ErrorCodeAsync());
    }

    public static TheoryData<string> BrokenSettings() => new()
    {
        """{"averageKgPerCage":"sixty","daysBeforeAutoClose":3,"rowVersion":"AAAAAAAAAAA="}""",
        """{"averageKgPerCage":1e400,"daysBeforeAutoClose":3,"rowVersion":"AAAAAAAAAAA="}""",
        """{"averageKgPerCage":60,"daysBeforeAutoClose":99999999999,"rowVersion":"AAAAAAAAAAA="}""",
        """{"averageKgPerCage":60,"daysBeforeAutoClose":true,"rowVersion":"AAAAAAAAAAA="}""",
        """{"averageKgPerCage":60,"daysBeforeAutoClose":3,"rowVersion":123}""",
        """not json""",
    };

    [Theory]
    [MemberData(nameof(BrokenSettings))]
    public async Task A_broken_settings_body_answers_400_and_changes_nothing(string body)
    {
        var owner = await factory.OwnerAsync();
        var before = await owner.Client.HarvestSettingsAsync();

        var answer = await owner.Client.PutAsync("/api/harvest-settings", new StringContent(body, Encoding.UTF8, "application/json"));

        Assert.Equal(HttpStatusCode.BadRequest, answer.StatusCode);
        Assert.Equal(before.GetRawText(), (await owner.Client.HarvestSettingsAsync()).GetRawText());
    }

    // Every view that shows a lane gets its state from the lots (BR-FARM-03).
    [Fact]
    public async Task The_raft_view_progress_seeding_and_harvest_screens_agree_on_the_lane_states_after_a_lot()
    {
        var raft = await factory.SeededRaftAsync(1, 2, 3);
        var owner = raft.Owner.Client;

        await owner.HarvestLotAsync(raft.RaftId, HarvestFields.OfLanes(1, 2).FinishedLanes(2));

        var screen = await owner.HarvestScreenAsync(raft.RaftId);
        var progress = (await owner.ProgressAsync(raft.RaftId)).GetProperty("lanes").EnumerateArray().ToDictionary(l => l.GetProperty("laneNumber").GetInt32(), l => l.Text("state"));
        var seeding = await owner.SeedingListAsync(raft.CycleId);
        foreach (var (lane, state) in new[] { (1, "BeingHarvested"), (2, "Harvested"), (3, "Growing"), (4, "NotSeeded") })
        {
            Assert.Equal(state, screen.HarvestStateOf(lane));
            Assert.Equal(state, progress[lane]);
            Assert.Equal(state, seeding.StateOf(lane));
        }
    }

    // BR-FARM-04: a lane removed after the cycle keeps its records: the lot still names it, and the view of that cycle still shows it with the lots.
    [Fact]
    public async Task A_lane_removed_after_the_close_stays_on_the_lot_and_on_the_view_of_its_cycle()
    {
        var raft = await factory.SeededRaftAsync();
        var owner = raft.Owner.Client;
        var lot = await owner.HarvestLotAsync(raft.RaftId, HarvestFields.OfLanes(5).FinishedLanes(5));
        await factory.CloseCycleAsync(raft.CycleId);
        var laneId = await factory.QueryAsync(db => db.Lanes.Where(l => l.RaftId == raft.RaftId && l.LaneNumber == 5).Select(l => l.Id).SingleAsync());
        await factory.UpdateAsync<Lane>(laneId, l => l.StatusId = StatusIds.Lane.Removed);

        Assert.Equal([5], (await owner.GetHarvestLotAsync(lot.Id())).Numbers("laneNumbers"));
        var lane = await owner.GetFromJsonAsync<JsonElement>($"/api/rafts/{raft.RaftId}/lanes/5/progress?cycleId={raft.CycleId}");
        Assert.Equal([lot.Text("lotCode")], lane.GetProperty("harvestLots").EnumerateArray().Select(l => l.Text("lotCode")));
    }

    // BR-CYCLE-01: the start date cannot pass the first session, so no lot ever comes before the start of its cycle.
    [Fact]
    public async Task With_a_lot_the_start_date_cannot_move_past_the_first_session_and_can_move_earlier()
    {
        var raft = await factory.SeededRaftAsync();
        var owner = raft.Owner.Client;
        var saved = await owner.RecordHarvestAsync(raft.RaftId);
        var version = saved.Text("cycleRowVersion");

        var late = await (await owner.PutCycleAsync(raft.CycleId, new CycleBody(Today, RowVersion: version))).ReadAsync(HttpStatusCode.BadRequest);
        Assert.Equal("MSG-GEN-01", late.Text("code"));

        var earlier = await (await owner.PutCycleAsync(raft.CycleId, new CycleBody(Today.AddDays(-20), RowVersion: version))).ReadAsync(HttpStatusCode.OK);
        Assert.Equal(Today.AddDays(-20), earlier.GetProperty("cycle").Date("startDate"));
    }

    // BR-HARVEST-03: the code uses the raft code as it stands, whatever its case, and the zone is not repeated.
    [Theory]
    [InlineData("KV01", "kv01-b01", "kv01-b01")]
    [InlineData("KV-01", "KV-01-B-02", "KV-01-B-02")]
    [InlineData("KV-01", "B-02", "KV-01-B-02")]
    [InlineData("KV01", "KV01", "KV01")]
    public async Task The_code_of_a_dashed_or_lower_case_raft_is_the_rule_of_br_harvest_03(string zoneCode, string raftCode, string start)
    {
        var owner = await factory.OwnerAsync();
        var suffix = NewCode();
        var zone = zoneCode + "X" + suffix;
        var raftBase = raftCode.Replace(zoneCode, zone, StringComparison.OrdinalIgnoreCase);
        var zoneId = (await owner.Client.CreateZoneAsync(NewZoneBody(zone))).Id();
        var raftId = (await owner.Client.CreateRaftAsync(zoneId, NewRaftBody(raftBase))).Id();
        var cycleId = (await owner.Client.StartCycleAsync(raftId, new CycleBody(Today.AddDays(-10)))).Id();
        await owner.Client.SeedingRecordAsync(cycleId, (await owner.Client.SeedLotAsync()).Session([1]));

        var lot = await owner.Client.HarvestLotAsync(raftId);

        var expectedStart = start.Replace(zoneCode, zone, StringComparison.OrdinalIgnoreCase);
        Assert.Equal($"{(raftCode == start ? raftBase : expectedStart)}-{lot.Date("harvestDate"):yyyyMMdd}", lot.Text("lotCode"));
    }

    // Lanes beyond the raft, an old lane number of another raft, and lanes of a raft whose lanes differ.
    [Fact]
    public async Task A_lane_of_another_raft_cannot_be_named_by_number_when_this_raft_is_shorter()
    {
        var owner = await factory.OwnerAsync();
        var zoneId = (await owner.Client.CreateZoneAsync()).Id();
        var shortRaft = (await owner.Client.CreateRaftAsync(zoneId, NewRaftBody(lanes: 2))).Id();
        var longRaft = (await owner.Client.CreateRaftAsync(zoneId, NewRaftBody(lanes: 6))).Id();
        foreach (var raftId in new[] { shortRaft, longRaft })
        {
            var cycleId = (await owner.Client.StartCycleAsync(raftId, new CycleBody(Today.AddDays(-10)))).Id();
            await owner.Client.SeedingRecordAsync(cycleId, (await owner.Client.SeedLotAsync()).Session([1, 2]));
        }

        var answer = await (await owner.Client.PostHarvestAsync(shortRaft, HarvestFields.OfLanes(6))).ReadAsync(HttpStatusCode.BadRequest);

        Assert.Equal("Lanes", answer.Text("field"));
    }

    // CYCLE-05 with its new field holds nothing per lane that AC-03 forbids, and the Accountant is not let in through it.
    [Fact]
    public async Task The_lane_view_shows_the_lots_to_who_may_see_the_harvest_only()
    {
        var raft = await factory.SeededRaftAsync();
        var lot = await raft.Owner.Client.HarvestLotAsync(raft.RaftId);
        var accountant = await factory.StaffAsync(null, StaffRole.Accountant);
        var elsewhere = await factory.ManagerAsync((await raft.Owner.Client.CreateZoneAsync()).Id());

        await AssertForbiddenAsync(await accountant.Client.GetAsync($"/api/rafts/{raft.RaftId}/lanes/1/progress"));
        await AssertForbiddenAsync(await elsewhere.Client.GetAsync($"/api/rafts/{raft.RaftId}/lanes/1/progress"));
        await AssertForbiddenAsync(await (await factory.TraderAsync()).GetAsync($"/api/rafts/{raft.RaftId}/lanes/1/progress"));
        var lane = await raft.Owner.Client.GetFromJsonAsync<JsonElement>($"/api/rafts/{raft.RaftId}/lanes/1/progress");
        Assert.Equal(lot.Id(), lane.GetProperty("harvestLots")[0].Id());
    }

    // BR-HARVEST-03 again: the audit log and the notification copy the lot code, so they must hold every code the lot code column holds.
    [Fact]
    public async Task M1_the_columns_that_copy_a_lot_code_hold_what_the_lot_code_column_holds()
    {
        var (lot, audit, notice) = await factory.QueryAsync(db => Task.FromResult((
            db.Model.FindEntityType(typeof(OMTS.Domain.Entities.Harvest.HarvestLot))!.FindProperty("LotCode")!.GetMaxLength(),
            db.Model.FindEntityType(typeof(OMTS.Domain.Entities.Platform.AuditEvent))!.FindProperty("RecordCode")!.GetMaxLength(),
            db.Model.FindEntityType(typeof(OMTS.Domain.Entities.Platform.Notification))!.FindProperty("RelatedRecordCode")!.GetMaxLength())));

        Assert.Equal((lot, lot), (audit, notice));
    }

    // The answers as the client list writes them: a renamed or missing field breaks the screens.
    [Fact]
    public async Task The_answers_have_the_fields_of_the_client_list()
    {
        var raft = await factory.SeededRaftAsync(1, 2);
        var owner = raft.Owner.Client;
        static string[] Names(JsonElement element) => element.EnumerateObject().Select(p => p.Name).Order(StringComparer.Ordinal).ToArray();
        static string[] Sorted(params string[] names) => names.Order(StringComparer.Ordinal).ToArray();

        var saved = await owner.RecordHarvestAsync(raft.RaftId, new HarvestFields { Lanes = ["1"], Photos = [Jpeg] });

        Assert.Equal(Sorted("lot", "everySeededLaneHarvested", "cycleRowVersion", "message", "messageCode"), Names(saved));
        Assert.Equal(("Harvest lot saved.", "MSG-GEN-05"), (saved.Text("message"), saved.Text("messageCode")));
        var lot = saved.GetProperty("lot");
        Assert.Equal(
            Sorted("id", "lotCode", "cycleId", "raftId", "raftCode", "zoneId", "zoneCode", "harvestedAt", "harvestDate", "laneNumbers", "finishedLaneNumbers", "cageCount", "kgPerCage", "estimatedKg", "figures", "statusId", "status", "reopenedOn", "closeDate", "leftoverKg", "closeReason", "originState", "publishedAt", "withdrawnAt", "originMissing", "canPublish", "photos", "recordedByAccountId", "recordedByName", "canCorrect", "canRemove", "canClose", "rowVersion", "productLines", "harvestPlan"),
            Names(lot));
        Assert.Equal(Sorted("invoicedKg", "returnedKg", "soldKg", "remainingKg", "lostKg", "differenceKg", "notFinal"), Names(lot.GetProperty("figures")));
        Assert.Equal(Sorted("key", "sortOrder", "url"), Names(lot.GetProperty("photos")[0]));
        Assert.Equal("NotPublished", lot.Text("originState"));
        Assert.Equal(Names(lot), Names(await owner.GetHarvestLotAsync(lot.Id())));

        var screen = await owner.HarvestScreenAsync(raft.RaftId);
        Assert.Equal(Sorted("raftId", "raftCode", "cycle", "averageKgPerCage", "canRecordHarvest", "lanes", "lots"), Names(screen));
        Assert.Equal(Sorted("id", "laneNumber", "statusId", "status", "state", "canHarvest"), Names(screen.GetProperty("lanes")[0]));
        Assert.Equal(
            Sorted("id", "lotCode", "harvestedAt", "harvestDate", "laneNumbers", "finishedLaneNumbers", "cageCount", "kgPerCage", "estimatedKg", "invoicedKg", "remainingKg", "statusId", "status", "originState", "originMissing", "canPublish", "canCorrect", "canRemove", "canClose", "rowVersion", "productLines", "harvestPlan"),
            Names(screen.GetProperty("lots")[0]));

        var settings = await owner.HarvestSettingsAsync();
        Assert.Equal(Sorted("averageKgPerCage", "daysBeforeAutoClose", "rowVersion"), Names(settings));
        var preview = await (await owner.GetAsync("/api/harvest-settings/auto-close-preview?days=1")).ReadAsync(HttpStatusCode.OK);
        Assert.Equal(Sorted("days", "lotCount", "lowered", "message", "messageCode"), Names(preview));
        Assert.Equal("MSG-HARVEST-03", preview.Text("messageCode"));
        var put = await (await owner.PutHarvestSettingsAsync(settings.Kg("averageKgPerCage"), 3, settings.Text("rowVersion"))).ReadAsync(HttpStatusCode.OK);
        Assert.Equal(Sorted("settings", "message", "messageCode"), Names(put));

        var lane = await owner.GetFromJsonAsync<JsonElement>($"/api/rafts/{raft.RaftId}/lanes/1/progress");
        Assert.Equal(Sorted("id", "lotCode", "harvestDate", "isFinished", "statusId", "status"), Names(lane.GetProperty("harvestLots")[0]));
    }

    [Fact]
    public async Task An_unknown_lot_or_raft_is_404_and_several_invalid_fields_name_the_first()
    {
        var raft = await factory.SeededRaftAsync();
        var owner = raft.Owner.Client;

        Assert.Equal(HttpStatusCode.NotFound, (await owner.GetAsync($"/api/harvest-lots/{Guid.NewGuid()}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await owner.GetAsync($"/api/rafts/{Guid.NewGuid()}/harvest-lots")).StatusCode);

        var answer = await (await owner.PostHarvestAsync(raft.RaftId, new HarvestFields { Lanes = ["99"], Cages = "0" })).ReadAsync(HttpStatusCode.BadRequest);
        Assert.Equal("Lanes", answer.Text("field"));
    }

    // The lanes shown: In Use, and Removed only with a record in the cycle (client list), with the raft view agreeing.
    [Fact]
    public async Task A_lane_removed_through_the_raft_form_after_the_close_stays_on_its_lot_and_a_lane_with_no_record_leaves_the_screen()
    {
        var raft = await factory.SeededRaftAsync(1, 5);
        var owner = raft.Owner.Client;
        var lot = await owner.HarvestLotAsync(raft.RaftId, HarvestFields.OfLanes(5).FinishedLanes(5));
        await factory.CloseCycleAsync(raft.CycleId);
        var view = await owner.GetFromJsonAsync<JsonElement>($"/api/rafts/{raft.RaftId}");
        var body = new RaftBody(view.Text("code"), 21.05m, 107.35m, "Raft row 2", "40 m x 20 m", null, 3, view.Text("rowVersion"));

        (await owner.PutAsJsonAsync($"/api/rafts/{raft.RaftId}", body)).EnsureSuccessStatusCode();

        Assert.Equal([5], (await owner.GetHarvestLotAsync(lot.Id())).Numbers("laneNumbers"));
        var lane = await owner.GetFromJsonAsync<JsonElement>($"/api/rafts/{raft.RaftId}/lanes/5/progress?cycleId={raft.CycleId}");
        Assert.Equal(lot.Id(), lane.GetProperty("harvestLots")[0].Id());
        // Closed today: the state of today still comes from that cycle, and lane 5 has its record.
        var numbers = (await owner.HarvestScreenAsync(raft.RaftId)).GetProperty("lanes").EnumerateArray().Select(l => l.GetProperty("laneNumber").GetInt32()).ToList();
        Assert.Equal([1, 2, 3, 5], numbers);
    }

    // FARM-04.1 too reads the lane states.
    [Fact]
    public async Task The_lanes_of_the_raft_today_show_the_states_from_the_lots()
    {
        var raft = await factory.SeededRaftAsync(1, 2, 3);
        var owner = raft.Owner.Client;
        await owner.HarvestLotAsync(raft.RaftId, HarvestFields.OfLanes(1, 2).FinishedLanes(2));

        var today = await owner.GetFromJsonAsync<JsonElement>($"/api/rafts/{raft.RaftId}/lanes/today");

        var states = today.GetProperty("lanes").EnumerateArray().ToDictionary(l => l.GetProperty("laneNumber").GetInt32(), l => l.Text("state"));
        Assert.Equal(("BeingHarvested", "Harvested", "Growing", "NotSeeded"), (states[1], states[2], states[3], states[4]));
    }

    // Two pairs of one manager on the zone give one reminder, and an Accountant pair here gives none.
    [Fact]
    public async Task A_manager_with_two_pairs_is_reminded_once_and_a_manager_elsewhere_who_is_accountant_here_not_at_all()
    {
        using var factory = new AuthApiFactory();
        var raft = await factory.SeededRaftAsync(1);
        var here = await factory.ZoneEntityAsync(raft.ZoneId);
        var elsewhere = await factory.ZoneEntityAsync((await raft.Owner.Client.CreateZoneAsync()).Id());
        var (twice, _) = await factory.StaffAccountAsync(here);
        await factory.GrantAsync(twice, StaffRole.ZoneManager, null);
        var (mixed, _) = await factory.StaffAccountAsync(elsewhere);
        await factory.GrantAsync(mixed, StaffRole.Accountant, here);

        await (await factory.LoginAsync(twice.Username)).HarvestLotAsync(raft.RaftId, HarvestFields.OfLanes(1).FinishedLanes(1));

        Assert.Equal([twice.Id], (await factory.NoticesAsync(ReminderType, raft.CycleId)).Select(n => n.RecipientAccountId!.Value));
    }
    // BR-HARVEST-03: "never changes". A raft code that is changed later leaves the code of the lot, and the code of a new lot follows the new raft code.
    [Fact]
    public async Task A_raft_code_changed_later_leaves_the_code_of_the_lot()
    {
        var raft = await factory.SeededRaftAsync();
        var owner = raft.Owner.Client;
        var lot = await owner.HarvestLotAsync(raft.RaftId);
        var view = await owner.GetFromJsonAsync<JsonElement>($"/api/rafts/{raft.RaftId}");
        var newCode = NewCode();
        var edit = NewRaftBody(newCode) with { RowVersion = view.Text("rowVersion") };

        var answer = await owner.PutAsJsonAsync($"/api/rafts/{raft.RaftId}", edit);

        Assert.True(answer.IsSuccessStatusCode || answer.StatusCode == HttpStatusCode.Conflict, $"{(int)answer.StatusCode}: {await answer.Content.ReadAsStringAsync()}");
        var again = await owner.GetHarvestLotAsync(lot.Id());
        Assert.Equal(lot.Text("lotCode"), again.Text("lotCode"));
    }
}

/// <summary>QA of PR H1a: what needs the clock of each test, so a run may cross midnight.</summary>
public class HarvestLotsQaTestsClock
{
    private static DateTimeOffset InVietnam(int month, int day, int hour = 9, int minute = 0) => new(2026, month, day, hour, minute, 0, TimeSpan.FromHours(7));

    private static async Task<(HttpClient Owner, Guid RaftId, Guid CycleId)> RaftAsync(CyclesQaClockApiFactory api)
    {
        var owner = (await api.OwnerAsync()).Client;
        api.Clock.At = InVietnam(10, 1);
        var zoneId = (await owner.CreateZoneAsync(NewZoneBody("KV01"))).Id();
        var raftId = (await owner.CreateRaftAsync(zoneId, NewRaftBody("KV01-B01"))).Id();
        var cycleId = (await owner.StartCycleAsync(raftId, new CycleBody(new DateOnly(2026, 10, 1)))).Id();
        await owner.RecordSeedingAsync(cycleId, (await owner.SeedLotAsync(received: new DateOnly(2026, 10, 1))).Session([1, 2, 3, 4, 5], new DateOnly(2026, 10, 1)));
        return (owner, raftId, cycleId);
    }

    // BR-CYCLE-06, PM 03/11: "start 01/10 and last harvest lot 10/10 give 10 days". The view still passes no last lot: "Harvest lots come with H1a: until then no cycle has a last lot".
    [Fact]
    public async Task M2_the_days_of_farming_of_a_closed_cycle_run_to_its_last_harvest_lot()
    {
        using var api = new CyclesQaClockApiFactory();
        var (owner, raftId, cycleId) = await RaftAsync(api);
        api.Clock.At = InVietnam(10, 5);
        await owner.HarvestLotAsync(raftId, HarvestFields.OfLanes(1));
        api.Clock.At = InVietnam(10, 10);
        await owner.HarvestLotAsync(raftId, HarvestFields.OfLanes(2));
        api.Clock.At = InVietnam(10, 20);
        await api.UpdateAsync<CultivationCycle>(cycleId, c =>
        {
            c.StatusId = StatusIds.CultivationCycle.Closed;
            c.CloseDate = new DateOnly(2026, 10, 20);
        });

        Assert.Equal(10, (await owner.GetFromJsonAsync<JsonElement>($"/api/cultivation-cycles/{cycleId}")).GetProperty("daysOfFarming").GetInt32());
        Assert.Equal(10, (await owner.ProgressAsync(raftId, $"?cycleId={cycleId}")).GetProperty("cycle").GetProperty("daysOfFarming").GetInt32());
    }

    // An Open cycle counts to today (PM 03/11), whatever its lots.
    [Fact]
    public async Task The_days_of_farming_of_an_open_cycle_run_to_today_with_a_lot()
    {
        using var api = new CyclesQaClockApiFactory();
        var (owner, raftId, cycleId) = await RaftAsync(api);
        api.Clock.At = InVietnam(10, 10);
        await owner.HarvestLotAsync(raftId, HarvestFields.OfLanes(1));
        api.Clock.At = InVietnam(10, 20);

        Assert.Equal(20, (await owner.GetFromJsonAsync<JsonElement>($"/api/cultivation-cycles/{cycleId}")).GetProperty("daysOfFarming").GetInt32());
    }

    // The preview on the days of GFR-05: with 3 days, a lot harvested on 26/10 can be sold until the end of 29/10 and is closed by the check at the start of 30/10.
    [Theory]
    [InlineData(28, 0)]
    [InlineData(29, 1)]
    [InlineData(30, 1)]
    public async Task The_preview_counts_the_lot_of_the_gfr_05_example_from_the_day_before_its_close(int today, int count)
    {
        using var api = new CyclesQaClockApiFactory();
        var (owner, raftId, _) = await RaftAsync(api);
        api.Clock.At = InVietnam(10, 26);
        await owner.HarvestLotAsync(raftId);
        api.Clock.At = InVietnam(10, today);

        var preview = await (await owner.GetAsync("/api/harvest-settings/auto-close-preview?days=3")).ReadAsync(HttpStatusCode.OK);

        Assert.Equal(count, preview.GetProperty("lotCount").GetInt32());
    }
}
