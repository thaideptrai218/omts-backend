using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using OMTS.Api.IntegrationTests.Cultivation;
using OMTS.Api.IntegrationTests.Harvest;
using OMTS.Api.IntegrationTests.Infrastructure;
using OMTS.Domain.Entities.Farming;
using OMTS.Domain.Enums;
using static OMTS.Api.IntegrationTests.Farming.FarmingTestKit;
using static OMTS.Api.IntegrationTests.Infrastructure.AcsTestKit;

namespace OMTS.Api.IntegrationTests.Farming;

/// <summary>
/// Decision of 12/11/2026, a lane that is not as it should be: report it damaged (seeding and harvest blocked, state kept), repair it from the stock (the lane is as before), or put it out of
/// service (a loss event with no quantity, the lane counts as finished at the close). Numbers never change, nothing is replaced, nothing is removed from the middle.
/// </summary>
public class LaneIncidentTests(AuthApiFactory factory) : IClassFixture<AuthApiFactory>
{
    private static string Url(Guid raftId, int lane, string action) => $"/api/rafts/{raftId}/lanes/{lane}/{action}";

    private static async Task<string> RaftVersionAsync(HttpClient client, Guid raftId) =>
        (await client.GetFromJsonAsync<JsonElement>($"/api/rafts/{raftId}")).Text("rowVersion");

    // A damage report is a multipart form since 12/11/2026: the note and 1 to 3 on-site photos.
    private static async Task<HttpResponseMessage> ReportAsync(HttpClient client, Guid raftId, int lane, string reason = "Line snapped in the storm", string? code = "LineBroken", int photos = 1) =>
        await client.PostAsync(Url(raftId, lane, "report-damage"), StepFormKit.FormOf(new { reason, reasonCode = code, rowVersion = await RaftVersionAsync(client, raftId) }, photos));

    private static async Task<HttpResponseMessage> RepairAsync(HttpClient client, Guid raftId, int lane, string reason = "New line tied", object[]? materials = null) =>
        await client.PostAsJsonAsync(Url(raftId, lane, "repair"), new { reason, materials = materials ?? [], rowVersion = await RaftVersionAsync(client, raftId) });

    private static async Task<HttpResponseMessage> OutOfServiceAsync(HttpClient client, Guid raftId, int lane, bool confirm = false, string reason = "Beyond repair") =>
        await client.PostAsJsonAsync(Url(raftId, lane, "out-of-service"), new { reason, reasonCode = "Other", confirm, rowVersion = await RaftVersionAsync(client, raftId) });

    private static async Task<Guid> MaterialWithStockAsync(HttpClient owner, decimal onHand) =>
        (await (await owner.PostAsJsonAsync("/api/farming-input-items", new { name = $"Rope {NewCode()}", group = "Material", unit = "m", costPrice = 100m, openingQuantity = onHand })).ReadAsync(HttpStatusCode.Created)).Id();

    private static JsonElement LaneOf(JsonElement screen, int number) =>
        screen.GetProperty("lanes").EnumerateArray().Single(l => l.GetProperty("laneNumber").GetInt32() == number);

    [Fact]
    public async Task A_damaged_lane_keeps_its_state_and_is_blocked_for_harvest_with_a_reason_code_and_the_history_says_who_and_why()
    {
        var raft = await factory.SeededRaftAsync();
        var owner = raft.Owner.Client;

        var saved = await (await ReportAsync(owner, raft.RaftId, 2)).ReadAsync(HttpStatusCode.OK);

        Assert.Equal((true, "Growing", true, "LaneDamaged"), (saved.Flag("changed"), saved.GetProperty("lane").Text("state"), saved.GetProperty("lane").Flag("isDamaged"), saved.GetProperty("lane").Text("reasonCode")));
        Assert.Equal(("Damaged", "LineBroken", "Line snapped in the storm", "Growing"), (saved.GetProperty("incident").Text("kind"), saved.GetProperty("incident").Text("reasonCode"),
            saved.GetProperty("incident").Text("reason"), saved.GetProperty("incident").Text("stateBefore")));
        // Lane 2 is seeded 100 %: the damage entry says the lane had seed (QA of 13/11/2026 saw false).
        Assert.True(saved.GetProperty("incident").Flag("hadSeed"));
        var screen = await owner.HarvestScreenAsync(raft.RaftId);
        var lane = LaneOf(screen, 2);
        Assert.Equal((false, "LaneDamaged", true, "Growing"), (lane.Flag("canHarvest"), lane.Text("reasonCode"), lane.Flag("isDamaged"), lane.Text("state")));
        Assert.True(LaneOf(screen, 1).Flag("canHarvest"));

        var refused = await (await owner.PostHarvestAsync(raft.RaftId, HarvestFields.OfLanes(1, 2))).ReadAsync(HttpStatusCode.BadRequest);
        Assert.Equal("Lanes", refused.Text("field"));
        Assert.Equal(0, await factory.LotCountAsync(raft.RaftId));

        var history = await owner.GetFromJsonAsync<JsonElement>($"/api/rafts/{raft.RaftId}/lanes/2/incidents");
        var entry = Assert.Single(history.GetProperty("incidents").EnumerateArray());
        Assert.Equal((2, "Damaged"), (entry.GetProperty("laneNumber").GetInt32(), entry.Text("kind")));
        Assert.Equal(raft.Owner.Account.Id, entry.GetProperty("recordedByAccountId").GetGuid());
        var audit = await factory.QueryAsync(db => Task.FromResult(db.AuditEvents.Single(a => a.Action == "Lane.Damaged" && a.RecordId == entry.GetProperty("laneId").GetGuid())));
        Assert.Equal("Line snapped in the storm", audit.Reason);
    }

    [Fact]
    public async Task Reporting_a_damaged_lane_again_changes_nothing_and_says_so()
    {
        var raft = await factory.SeededRaftAsync();
        var owner = raft.Owner.Client;
        var first = await (await ReportAsync(owner, raft.RaftId, 3)).ReadAsync(HttpStatusCode.OK);

        var again = await (await ReportAsync(owner, raft.RaftId, 3, "Reported twice")).ReadAsync(HttpStatusCode.OK);

        Assert.Equal((false, first.GetProperty("incident").Id()), (again.Flag("changed"), again.GetProperty("incident").Id()));
        Assert.Equal(1, await factory.QueryAsync(db => db.LaneIncidents.CountAsync(i => i.Lane.RaftId == raft.RaftId && i.Kind == LaneIncidentKind.Damaged)));
    }

    [Fact]
    public async Task A_reason_is_required_and_an_unknown_reason_code_is_refused()
    {
        var raft = await factory.SeededRaftAsync();
        var owner = raft.Owner.Client;

        Assert.Equal(["Reason"], await (await ReportAsync(owner, raft.RaftId, 1, " ")).InvalidFieldsAsync());
        Assert.Equal(["Reason code"], await (await ReportAsync(owner, raft.RaftId, 1, "Broken", "Sabotage")).InvalidFieldsAsync());
        Assert.Equal(0, await factory.QueryAsync(db => db.LaneIncidents.CountAsync(i => i.Lane.RaftId == raft.RaftId)));
    }

    [Fact]
    public async Task A_lane_with_no_seed_can_be_reported_damaged_but_is_not_offered_for_seeding_and_a_session_on_it_is_refused()
    {
        var raft = await factory.SeededRaftAsync(1, 2);
        var owner = raft.Owner.Client;
        var damage = await (await ReportAsync(owner, raft.RaftId, 4)).ReadAsync(HttpStatusCode.OK);
        Assert.False(damage.GetProperty("incident").Flag("hadSeed"));

        var screen = await owner.GetFromJsonAsync<JsonElement>($"/api/cultivation-cycles/{raft.CycleId}/seeding-records");
        var lane4 = screen.GetProperty("lanes").EnumerateArray().Single(l => l.GetProperty("laneNumber").GetInt32() == 4);
        Assert.Equal((false, "LaneDamaged"), (lane4.Flag("canSeed"), lane4.Text("reasonCode")));

        var refused = await owner.PostSeedingAsync(raft.CycleId, raft.Lot.Session([4]));
        Assert.Equal("Lanes", (await refused.ReadAsync(HttpStatusCode.BadRequest)).Text("field"));
    }

    [Fact]
    public async Task A_repair_clears_the_flag_and_the_lane_is_exactly_as_before()
    {
        var raft = await factory.SeededRaftAsync();
        var owner = raft.Owner.Client;
        var damaged = await (await ReportAsync(owner, raft.RaftId, 2)).ReadAsync(HttpStatusCode.OK);

        var repaired = await (await RepairAsync(owner, raft.RaftId, 2)).ReadAsync(HttpStatusCode.OK);

        Assert.Equal(("Repaired", "Growing", false), (repaired.GetProperty("incident").Text("kind"), repaired.GetProperty("lane").Text("state"), repaired.GetProperty("lane").Flag("isDamaged")));
        Assert.Equal(damaged.GetProperty("incident").Id(), repaired.GetProperty("incident").GetProperty("resolvesIncidentId").GetGuid());
        Assert.True(LaneOf(await owner.HarvestScreenAsync(raft.RaftId), 2).Flag("canHarvest"));
        await owner.RecordHarvestAsync(raft.RaftId, HarvestFields.OfLanes(1, 2));
        var history = (await owner.GetFromJsonAsync<JsonElement>($"/api/rafts/{raft.RaftId}/lane-history?laneNumber=2")).GetProperty("incidents");
        Assert.Equal(["Repaired", "Damaged"], history.EnumerateArray().Select(i => i.Text("kind")).ToList()); // newest first
        Assert.Empty((await owner.GetFromJsonAsync<JsonElement>($"/api/rafts/{raft.RaftId}/lane-history?laneNumber=1")).GetProperty("incidents").EnumerateArray());
    }

    [Fact]
    public async Task A_repair_of_a_lane_that_is_not_damaged_answers_lane_not_damaged()
    {
        var raft = await factory.SeededRaftAsync();

        var problem = await (await RepairAsync(raft.Owner.Client, raft.RaftId, 1)).ReadAsync(HttpStatusCode.Conflict);

        Assert.Equal(("MSG-GEN-09", "LANE_NOT_DAMAGED"), (problem.Text("code"), problem.Text("reasonCode")));
    }

    [Fact]
    public async Task A_repair_takes_its_materials_out_of_the_stock_in_one_save_and_the_history_names_the_issue()
    {
        var raft = await factory.SeededRaftAsync();
        var owner = raft.Owner.Client;
        var item = await MaterialWithStockAsync(owner, 10m);
        await (await ReportAsync(owner, raft.RaftId, 5)).ReadAsync(HttpStatusCode.OK);

        var repaired = await (await RepairAsync(owner, raft.RaftId, 5, "Tied a new line", [new { itemId = item, quantity = 4m, unit = "m" }])).ReadAsync(HttpStatusCode.OK);

        var material = Assert.Single(repaired.GetProperty("incident").GetProperty("materials").EnumerateArray());
        Assert.Equal((item, 4m, "m"), (material.GetProperty("itemId").GetGuid(), material.GetProperty("quantity").GetDecimal(), material.Text("unit")));
        Assert.StartsWith("XD", material.Text("issueCode"));
        var issueId = material.GetProperty("issueId").GetGuid();
        var issue = await factory.QueryAsync(db => db.InputIssues.Include(i => i.Lines).SingleAsync(i => i.Id == issueId));
        Assert.Equal((OMTS.Domain.Constants.StatusIds.InputIssue.Completed, raft.RaftId, InputIssueSource.MaterialUsage, 4m), (issue.StatusId, issue.RaftId, issue.SourceKind, Assert.Single(issue.Lines).Quantity));
        var usage = await factory.QueryAsync(db => db.MaterialUsages.SingleAsync(u => u.IssueId == issueId));
        Assert.Equal((raft.RaftId, item, 4m), (usage.RaftId, usage.ItemId, usage.Quantity));
        Assert.Contains("Lane 5", usage.RepairReason);
        Assert.Equal(1, await factory.QueryAsync(db => db.LaneIncidentMaterials.CountAsync(m => m.IssueId == issueId && m.MaterialUsageId == usage.Id)));
    }

    [Fact]
    public async Task A_repair_with_more_material_than_is_on_hand_is_refused_with_msg_input_01_and_saves_nothing()
    {
        var raft = await factory.SeededRaftAsync();
        var owner = raft.Owner.Client;
        var settings = await owner.GetFromJsonAsync<JsonElement>("/api/input-inventory-settings");
        await (await owner.PutAsJsonAsync("/api/input-inventory-settings", new { costMethod = "Fixed", allowNegativeQuantity = false, rowVersion = settings.Text("rowVersion") })).ReadAsync(HttpStatusCode.OK);
        var item = await MaterialWithStockAsync(owner, 3m);
        await (await ReportAsync(owner, raft.RaftId, 5)).ReadAsync(HttpStatusCode.OK);

        var refused = await (await RepairAsync(owner, raft.RaftId, 5, materials: [new { itemId = item, quantity = 4m }])).ReadAsync(HttpStatusCode.Conflict);

        Assert.Equal(("MSG-INPUT-01", -1m), (refused.Text("code"), refused.GetProperty("balanceAfter").GetDecimal()));
        Assert.Equal(0, await factory.QueryAsync(db => db.MaterialUsages.CountAsync(u => u.RaftId == raft.RaftId)));
        Assert.Equal(0, await factory.QueryAsync(db => db.LaneIncidents.CountAsync(i => i.Lane.RaftId == raft.RaftId && i.Kind == LaneIncidentKind.Repaired)));
        Assert.True(LaneOf(await owner.GetFromJsonAsync<JsonElement>($"/api/rafts/{raft.RaftId}/lanes/today"), 5).Flag("isDamaged"));
    }

    [Fact]
    public async Task A_repair_refuses_a_second_line_for_the_same_item_a_wrong_unit_and_an_item_that_is_not_a_material()
    {
        var raft = await factory.SeededRaftAsync();
        var owner = raft.Owner.Client;
        var item = await MaterialWithStockAsync(owner, 10m);
        await (await ReportAsync(owner, raft.RaftId, 5)).ReadAsync(HttpStatusCode.OK);

        Assert.Contains("Line 2 item", await (await RepairAsync(owner, raft.RaftId, 5, materials: [new { itemId = item, quantity = 1m }, new { itemId = item, quantity = 1m }])).InvalidFieldsAsync());
        Assert.Contains("Line 1 unit", await (await RepairAsync(owner, raft.RaftId, 5, materials: [new { itemId = item, quantity = 1m, unit = "kg" }])).InvalidFieldsAsync());
        Assert.Contains("Line 1 item", await (await RepairAsync(owner, raft.RaftId, 5, materials: [new { itemId = raft.Lot.ItemId, quantity = 1m }])).InvalidFieldsAsync());
        Assert.Contains("Line 1 quantity", await (await RepairAsync(owner, raft.RaftId, 5, materials: [new { itemId = item, quantity = 0m }])).InvalidFieldsAsync());
    }

    [Fact]
    public async Task A_lane_with_seed_asks_before_it_is_put_out_of_service_and_then_the_loss_is_recorded_with_no_quantity()
    {
        var raft = await factory.SeededRaftAsync();
        var owner = raft.Owner.Client;

        var question = await (await OutOfServiceAsync(owner, raft.RaftId, 3)).ReadAsync(HttpStatusCode.Conflict);

        Assert.Equal(("MSG-GEN-04", "confirm"), (question.Text("code"), question.Text("confirmFlag")));
        Assert.Equal([raft.Lot.Code], question.GetProperty("seedLotCodes").EnumerateArray().Select(c => c.GetString()).ToList());
        Assert.Equal(0, await factory.QueryAsync(db => db.LaneIncidents.CountAsync(i => i.Lane.RaftId == raft.RaftId)));

        var saved = await (await OutOfServiceAsync(owner, raft.RaftId, 3, confirm: true)).ReadAsync(HttpStatusCode.OK);

        var incident = saved.GetProperty("incident");
        Assert.Equal(("OutOfService", true, "Growing"), (incident.Text("kind"), incident.Flag("hadSeed"), incident.Text("stateBefore")));
        Assert.Equal(("OutOfService", true, "LaneOutOfService"), (saved.GetProperty("lane").Text("state"), saved.GetProperty("lane").Flag("isOutOfService"), saved.GetProperty("lane").Text("reasonCode")));
        var lane = LaneOf(await owner.HarvestScreenAsync(raft.RaftId), 3);
        Assert.Equal((false, "LaneOutOfService"), (lane.Flag("canHarvest"), lane.Text("reasonCode")));
        var number = (await owner.GetFromJsonAsync<JsonElement>($"/api/rafts/{raft.RaftId}")).GetProperty("lanes")[2];
        Assert.Equal((3, true), (number.GetProperty("laneNumber").GetInt32(), number.Flag("isOutOfService")));
    }

    [Fact]
    public async Task A_lane_out_of_service_is_finished_for_the_close_so_the_cycle_closes_normally_and_the_summary_names_the_loss()
    {
        var raft = await factory.SeededRaftAsync();
        var owner = raft.Owner.Client;
        await (await OutOfServiceAsync(owner, raft.RaftId, 3, confirm: true, reason: "Storm tore the lane")).ReadAsync(HttpStatusCode.OK);
        var open = await owner.ClosePreviewAsync(raft.CycleId);
        Assert.True(open.Flag("isEarly")); // four lanes still grow

        await owner.RecordHarvestAsync(raft.RaftId, HarvestFields.OfLanes(1, 2, 4, 5).FinishedLanes(1, 2, 4, 5));

        var preview = await owner.ClosePreviewAsync(raft.CycleId);
        Assert.False(preview.Flag("isEarly"));
        var summary = preview.GetProperty("summary");
        Assert.Empty(summary.GetProperty("notHarvestedLanes").EnumerateArray());
        var lost = Assert.Single(summary.GetProperty("lostLanes").EnumerateArray());
        Assert.Equal((3, "Storm tore the lane"), (lost.GetProperty("laneNumber").GetInt32(), lost.Text("reason")));
        var closed = await owner.CloseCycleAsync(raft.CycleId);
        Assert.False(closed.GetProperty("cycle").Flag("closedEarly"));
    }

    [Fact]
    public async Task The_last_lane_put_out_of_service_gives_the_same_close_reminder_as_the_last_lot()
    {
        var raft = await factory.SeededRaftAsync(1, 2);
        var owner = raft.Owner.Client;
        await owner.RecordHarvestAsync(raft.RaftId, HarvestFields.OfLanes(1).FinishedLanes(1));

        var saved = await (await OutOfServiceAsync(owner, raft.RaftId, 2, confirm: true)).ReadAsync(HttpStatusCode.OK);

        Assert.True(saved.Flag("everySeededLaneHarvested"));
        Assert.NotNull(await factory.QueryAsync(db => db.CultivationCycles.Where(c => c.Id == raft.CycleId).Select(c => c.CloseRemindedAt).SingleAsync()));
    }

    [Fact]
    public async Task A_lane_with_no_seed_goes_out_of_service_without_a_question_and_is_no_loss()
    {
        var raft = await factory.SeededRaftAsync(1, 2);
        var owner = raft.Owner.Client;

        var saved = await (await OutOfServiceAsync(owner, raft.RaftId, 5)).ReadAsync(HttpStatusCode.OK);

        Assert.Equal((false, "NotSeeded", true), (saved.GetProperty("incident").Flag("hadSeed"), saved.GetProperty("lane").Text("state"), saved.GetProperty("lane").Flag("isOutOfService")));
        var preview = await owner.ClosePreviewAsync(raft.CycleId);
        Assert.Empty(preview.GetProperty("summary").GetProperty("lostLanes").EnumerateArray());
    }

    [Fact]
    public async Task A_lane_out_of_service_cannot_be_reported_or_repaired_and_putting_it_out_again_is_safe()
    {
        var raft = await factory.SeededRaftAsync();
        var owner = raft.Owner.Client;
        var first = await (await OutOfServiceAsync(owner, raft.RaftId, 3, confirm: true)).ReadAsync(HttpStatusCode.OK);

        var report = await (await ReportAsync(owner, raft.RaftId, 3)).ReadAsync(HttpStatusCode.Conflict);
        var repair = await (await RepairAsync(owner, raft.RaftId, 3)).ReadAsync(HttpStatusCode.Conflict);
        var again = await (await OutOfServiceAsync(owner, raft.RaftId, 3, confirm: true)).ReadAsync(HttpStatusCode.OK);

        Assert.Equal(("MSG-GEN-09", "LaneOutOfService"), (report.Text("code"), report.Text("reasonCode")));
        Assert.Equal("MSG-GEN-09", repair.Text("code"));
        Assert.Equal((false, first.GetProperty("incident").Id()), (again.Flag("changed"), again.GetProperty("incident").Id()));
        Assert.Equal(1, await factory.QueryAsync(db => db.LaneIncidents.CountAsync(i => i.Lane.RaftId == raft.RaftId && i.Kind == LaneIncidentKind.OutOfService)));
    }

    [Fact]
    public async Task A_damaged_lane_that_is_put_out_of_service_resolves_its_damage_report()
    {
        var raft = await factory.SeededRaftAsync();
        var owner = raft.Owner.Client;
        var damaged = await (await ReportAsync(owner, raft.RaftId, 2)).ReadAsync(HttpStatusCode.OK);

        var saved = await (await OutOfServiceAsync(owner, raft.RaftId, 2, confirm: true)).ReadAsync(HttpStatusCode.OK);

        Assert.Equal(damaged.GetProperty("incident").Id(), saved.GetProperty("incident").GetProperty("resolvesIncidentId").GetGuid());
        var raftView = await owner.GetFromJsonAsync<JsonElement>($"/api/rafts/{raft.RaftId}");
        Assert.Equal((0, 1), (raftView.GetProperty("damagedLaneCount").GetInt32(), raftView.GetProperty("outOfServiceLaneCount").GetInt32()));
    }

    [Fact]
    public async Task The_numbers_never_change_and_the_lane_count_still_follows_br_farm_04()
    {
        var raft = await factory.SeededRaftAsync();
        var owner = raft.Owner.Client;
        await (await OutOfServiceAsync(owner, raft.RaftId, 2, confirm: true)).ReadAsync(HttpStatusCode.OK);

        var view = await owner.GetFromJsonAsync<JsonElement>($"/api/rafts/{raft.RaftId}");

        Assert.Equal([1, 2, 3, 4, 5], view.GetProperty("lanes").EnumerateArray().Select(l => l.GetProperty("laneNumber").GetInt32()).ToList());
        Assert.Equal(5, view.GetProperty("laneCount").GetInt32());
        var fewer = await owner.PutAsJsonAsync($"/api/rafts/{raft.RaftId}", NewRaftBody(view.Text("code"), 4) with { RowVersion = view.Text("rowVersion") });
        Assert.Equal("MSG-GEN-06", (await fewer.ReadAsync(HttpStatusCode.Conflict)).Text("code"));
    }

    [Fact]
    public async Task A_zone_manager_may_report_and_the_owner_is_told_but_a_manager_of_another_zone_may_not()
    {
        var raft = await factory.SeededRaftAsync();
        var manager = await factory.ManagerAsync(raft.ZoneId);
        var other = await factory.ManagerAsync((await raft.Owner.Client.CreateZoneAsync()).Id());

        var refused = await other.Client.PostAsync(Url(raft.RaftId, 1, "report-damage"), StepFormKit.FormOf(new { reason = "x", rowVersion = "" }, photos: 1));
        Assert.Equal(HttpStatusCode.Forbidden, refused.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await other.Client.GetAsync($"/api/rafts/{raft.RaftId}/lane-history")).StatusCode);

        await (await ReportAsync(manager.Client, raft.RaftId, 1)).ReadAsync(HttpStatusCode.OK);
        var notices = await factory.NoticesAsync("LANE_DAMAGED", raft.RaftId);
        Assert.Contains(notices, n => n.RecipientAccountId == raft.Owner.Account.Id);
        await (await OutOfServiceAsync(manager.Client, raft.RaftId, 1, confirm: true)).ReadAsync(HttpStatusCode.OK);
        Assert.NotEmpty(await factory.NoticesAsync("LANE_OUT_OF_SERVICE", raft.RaftId));
    }

    [Fact]
    public async Task The_owner_is_not_told_of_what_the_owner_did()
    {
        var raft = await factory.SeededRaftAsync();

        await (await ReportAsync(raft.Owner.Client, raft.RaftId, 1)).ReadAsync(HttpStatusCode.OK);

        Assert.DoesNotContain(await factory.NoticesAsync("LANE_DAMAGED", raft.RaftId), n => n.RecipientAccountId == raft.Owner.Account.Id);
    }

    [Fact]
    public async Task A_discontinued_raft_and_an_unknown_lane_or_raft_are_refused()
    {
        var raft = await factory.SeededRaftAsync();
        var owner = raft.Owner.Client;

        Assert.Equal(HttpStatusCode.NotFound, (await ReportAsync(owner, raft.RaftId, 99)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await owner.PostAsync(Url(Guid.NewGuid(), 1, "report-damage"), StepFormKit.FormOf(new { reason = "x", rowVersion = "" }, photos: 1))).StatusCode);
        var version = await RaftVersionAsync(owner, raft.RaftId);
        await factory.DiscontinueRaftAsync(raft.RaftId);
        var readOnly = await owner.PostAsync(Url(raft.RaftId, 1, "report-damage"), StepFormKit.FormOf(new { reason = "x", rowVersion = version }, photos: 1));
        Assert.Equal("MSG-GEN-09", (await readOnly.ReadAsync(HttpStatusCode.Conflict)).Text("code"));
    }

    [Fact]
    public async Task A_removed_lane_cannot_be_reported()
    {
        var setup = await factory.RaftAsync();
        var owner = setup.Owner.Client;
        var raft = await owner.GetFromJsonAsync<JsonElement>($"/api/rafts/{setup.RaftId}");
        var fewer = await owner.EditRaftAsync(setup.RaftId, NewRaftBody(raft.Text("code"), 3) with { RowVersion = raft.Text("rowVersion") });

        var response = await ReportAsync(owner, setup.RaftId, 5);

        Assert.Equal(3, fewer.GetProperty("laneCount").GetInt32());
        Assert.Equal("MSG-GEN-09", (await response.ReadAsync(HttpStatusCode.Conflict)).Text("code"));
    }

    [Fact]
    public async Task An_incident_takes_up_to_three_photos_the_report_brings_one_the_others_come_one_at_a_time_and_a_fourth_is_refused()
    {
        var raft = await factory.SeededRaftAsync();
        var owner = raft.Owner.Client;
        var saved = await (await ReportAsync(owner, raft.RaftId, 2)).ReadAsync(HttpStatusCode.OK);
        var incidentId = saved.GetProperty("incident").Id();
        var url = $"/api/rafts/{raft.RaftId}/lanes/2/incidents/{incidentId}/photos";
        var filesBefore = factory.StoredFiles("lane-incident-photos").Count - 1;
        Assert.Equal(1, saved.GetProperty("incident").GetProperty("photos").GetArrayLength());

        for (var i = 2; i <= 3; i++)
        {
            var view = await (await owner.PostAsync(url, new MultipartFormDataContent { { FileContent(Png, "image/png"), "file", "lane.png" } })).ReadAsync(HttpStatusCode.Created);
            Assert.Equal(i, view.GetProperty("photos").GetArrayLength());
        }

        var fourth = await owner.PostAsync(url, new MultipartFormDataContent { { FileContent(Png, "image/png"), "file", "lane.png" } });
        Assert.Equal("Photos", (await fourth.ReadAsync(HttpStatusCode.BadRequest)).Text("field"));
        var notAPhoto = await owner.PostAsync(url, new MultipartFormDataContent { { FileContent(TextPretendingToBeAFile, "image/png"), "file", "lane.png" } });
        Assert.Equal(HttpStatusCode.BadRequest, notAPhoto.StatusCode);
        Assert.Equal(filesBefore + 3, factory.StoredFiles("lane-incident-photos").Count);
        var history = (await owner.GetFromJsonAsync<JsonElement>($"/api/rafts/{raft.RaftId}/lanes/2/incidents")).GetProperty("incidents")[0];
        Assert.Equal(3, history.GetProperty("photos").GetArrayLength());
    }

    [Fact]
    public async Task A_photo_of_another_lane_or_raft_is_not_found()
    {
        var raft = await factory.SeededRaftAsync();
        var owner = raft.Owner.Client;
        var incidentId = (await (await ReportAsync(owner, raft.RaftId, 2)).ReadAsync(HttpStatusCode.OK)).GetProperty("incident").Id();

        var wrongLane = await owner.PostAsync($"/api/rafts/{raft.RaftId}/lanes/3/incidents/{incidentId}/photos", new MultipartFormDataContent { { FileContent(Png, "image/png"), "file", "lane.png" } });

        Assert.Equal(HttpStatusCode.NotFound, wrongLane.StatusCode);
    }

    [Fact]
    public async Task The_harvest_of_a_lane_already_in_a_lot_can_still_be_corrected_after_the_lane_is_reported_damaged()
    {
        var raft = await factory.SeededRaftAsync();
        var owner = raft.Owner.Client;
        var lot = await owner.HarvestLotAsync(raft.RaftId, HarvestFields.OfLanes(1, 2));
        await (await ReportAsync(owner, raft.RaftId, 2)).ReadAsync(HttpStatusCode.OK);

        var corrected = await factory.CorrectLotAsync(owner, lot.Id(), new HarvestFields { Lanes = ["1", "2"], Cages = "9" });

        Assert.Equal(9m, corrected.GetProperty("lot").Kg("cageCount"));
        var addDamaged = await factory.SendCorrectionAsync(owner, lot.Id(), new HarvestFields { Lanes = ["1", "2", "3"], Cages = "9" });
        Assert.Equal(HttpStatusCode.OK, addDamaged.StatusCode);
        await (await ReportAsync(owner, raft.RaftId, 4)).ReadAsync(HttpStatusCode.OK);
        var refused = await factory.SendCorrectionAsync(owner, lot.Id(), new HarvestFields { Lanes = ["1", "2", "3", "4"], Cages = "9" });
        Assert.Equal("Lanes", (await refused.ReadAsync(HttpStatusCode.BadRequest)).Text("field"));
    }

    private static async Task<HttpResponseMessage> RestoreAsync(HttpClient client, Guid raftId, int lane, string? reason = null, object[]? materials = null) =>
        await client.PostAsJsonAsync(Url(raftId, lane, "restore-service"), new { reason, materials = materials ?? [], rowVersion = await RaftVersionAsync(client, raftId) });

    [Fact]
    public async Task A_repair_needs_no_reason_and_a_blank_one_reads_as_repaired()
    {
        var raft = await factory.SeededRaftAsync();
        var owner = raft.Owner.Client;
        await (await ReportAsync(owner, raft.RaftId, 2)).ReadAsync(HttpStatusCode.OK);

        var repaired = await (await RepairAsync(owner, raft.RaftId, 2, reason: " ")).ReadAsync(HttpStatusCode.OK);

        Assert.Equal(("Repaired", "Repaired"), (repaired.GetProperty("incident").Text("kind"), repaired.GetProperty("incident").Text("reason")));
    }

    [Fact]
    public async Task A_lane_out_of_service_cannot_come_back_while_the_open_cycle_holds_its_seed_and_the_answer_is_clear_vietnamese()
    {
        var raft = await factory.SeededRaftAsync();
        var owner = raft.Owner.Client;
        await (await OutOfServiceAsync(owner, raft.RaftId, 3, confirm: true)).ReadAsync(HttpStatusCode.OK);

        var refused = await (await RestoreAsync(owner, raft.RaftId, 3)).ReadAsync(HttpStatusCode.Conflict);

        Assert.Equal(("MSG-GEN-09", "LANE_HOLDS_SEED"), (refused.Text("code"), refused.Text("reasonCode")));
        Assert.StartsWith("Làn 3 chưa thể", refused.Text("detail"));
        Assert.True(LaneOf(await owner.GetFromJsonAsync<JsonElement>($"/api/rafts/{raft.RaftId}/lanes/today"), 3).Flag("isOutOfService"));
    }

    [Fact]
    public async Task After_the_cycle_is_closed_the_lane_is_restored_not_seeded_with_its_materials_taken_from_the_stock()
    {
        var raft = await factory.SeededRaftAsync();
        var owner = raft.Owner.Client;
        var item = await MaterialWithStockAsync(owner, 10m);
        await (await OutOfServiceAsync(owner, raft.RaftId, 3, confirm: true)).ReadAsync(HttpStatusCode.OK);
        await owner.RecordHarvestAsync(raft.RaftId, HarvestFields.OfLanes(1, 2, 4, 5).FinishedLanes(1, 2, 4, 5));
        await owner.CloseCycleAsync(raft.CycleId);
        await factory.UpdateAsync<OMTS.Domain.Entities.Cultivation.CultivationCycle>(raft.CycleId, c => c.CloseDate = Today.AddDays(-1)); // a record is refused on the close day itself (BR-CYCLE-04)

        var restored = await (await RestoreAsync(owner, raft.RaftId, 3, "New lane built", [new { itemId = item, quantity = 5m, unit = "m" }])).ReadAsync(HttpStatusCode.OK);

        Assert.Equal(("Restored", "NotSeeded", false, false), (restored.GetProperty("incident").Text("kind"), restored.GetProperty("lane").Text("state"),
            restored.GetProperty("lane").Flag("isOutOfService"), restored.GetProperty("lane").Flag("isDamaged")));
        var material = Assert.Single(restored.GetProperty("incident").GetProperty("materials").EnumerateArray());
        Assert.StartsWith("XD", material.Text("issueCode"));
        var kinds = (await owner.GetFromJsonAsync<JsonElement>($"/api/rafts/{raft.RaftId}/lanes/3/incidents")).GetProperty("incidents").EnumerateArray().Select(i => i.Text("kind")).ToList();
        Assert.Equal(["Restored", "OutOfService"], kinds);
        Assert.Equal(1, await factory.QueryAsync(db => db.AuditEvents.CountAsync(a => a.Action == "Lane.Restored" && a.RecordId == restored.GetProperty("lane").Id())));
        var again = await (await RestoreAsync(owner, raft.RaftId, 3)).ReadAsync(HttpStatusCode.Conflict);
        Assert.Equal("LANE_NOT_OUT_OF_SERVICE", again.Text("reasonCode"));
    }

    [Fact]
    public async Task A_restore_with_more_material_than_is_on_hand_is_refused_and_the_lane_stays_out_of_service()
    {
        var raft = await factory.SeededRaftAsync(1, 2);
        var owner = raft.Owner.Client;
        var settings = await owner.GetFromJsonAsync<JsonElement>("/api/input-inventory-settings");
        await (await owner.PutAsJsonAsync("/api/input-inventory-settings", new { costMethod = "Fixed", allowNegativeQuantity = false, rowVersion = settings.Text("rowVersion") })).ReadAsync(HttpStatusCode.OK);
        var item = await MaterialWithStockAsync(owner, 1m);
        await (await OutOfServiceAsync(owner, raft.RaftId, 5)).ReadAsync(HttpStatusCode.OK); // no seed: may come back during the open cycle

        var refused = await (await RestoreAsync(owner, raft.RaftId, 5, materials: [new { itemId = item, quantity = 4m }])).ReadAsync(HttpStatusCode.Conflict);
        Assert.Equal("MSG-INPUT-01", refused.Text("code"));
        Assert.True(LaneOf(await owner.GetFromJsonAsync<JsonElement>($"/api/rafts/{raft.RaftId}/lanes/today"), 5).Flag("isOutOfService"));

        var ok = await (await RestoreAsync(owner, raft.RaftId, 5)).ReadAsync(HttpStatusCode.OK);
        Assert.False(ok.GetProperty("lane").Flag("isOutOfService"));
    }
}
