using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using OMTS.Api.IntegrationTests.Infrastructure;
using OMTS.Domain.Constants;
using OMTS.Domain.Entities.Farming;
using OMTS.Domain.Enums;

namespace OMTS.Api.IntegrationTests.WaterQuality;

/// <summary>
/// WATER-02 as far as this model needs it: a correction changes the same record against its own thresholds, a void is
/// final, both need a reason and are audited. Who may do it and the notify matrix of WATER-02.2 come with OMTS-219 W2.
/// </summary>
public class WaterCorrectionTests(AuthApiFactory api) : IClassFixture<AuthApiFactory>
{
    [Fact]
    public async Task Correcting_into_range_changes_the_same_record_and_raises_no_new_alert()
    {
        var z = await api.ZoneAsync();
        var original = await z.RecordedAsync(9.1m, 20m);
        var alertsBefore = (await api.AlertsOfAsync(original.Id())).Count;

        var response = await z.Owner.CorrectAsync(original.Id(), 8.1m, 20m, reason: "Đọc nhầm 8.1 thành 9.1", temperature: 26m);
        var corrected = await response.Content.ReadFromJsonAsync<JsonElement>();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(original.Id(), corrected.Id());
        Assert.Equal(("Active", "None"), (corrected.Text("status"), corrected.Text("alertState")));
        Assert.Equal((8.1m, 26m), (corrected.GetProperty("ph").GetDecimal(), corrected.GetProperty("temperature").GetDecimal()));
        Assert.Equal(alertsBefore, (await api.AlertsOfAsync(original.Id())).Count);
        Assert.Equal(1, await api.QueryAsync(db => db.WaterMeasurements.CountAsync(m => m.ZoneId == z.Zone.Id)));
    }

    [Fact]
    public async Task Correction_keeps_zone_raft_photo_and_is_audited_once_with_before_after_and_reason()
    {
        var z = await api.ZoneAsync();
        var key = await z.Owner.PhotoKeyAsync(z.Zone.Id);
        var id = (await (await z.RecordAsync(7.4m, 21m, photoKey: key)).Content.ReadFromJsonAsync<JsonElement>()).Id();

        await z.Owner.CorrectAsync(id, 7.5m, 21.5m, reason: "Sai số");

        var stored = await api.QueryAsync(db => db.WaterMeasurements.AsNoTracking().SingleAsync(m => m.Id == id));
        Assert.Equal((key, z.Raft.Id, z.Zone.Id, 7.5m), (stored.PhotoUrl, stored.RaftId, stored.ZoneId, stored.Ph));
        var audit = Assert.Single(await api.AuditOfAsync(id), e => e.Action == "WaterMeasurement.Corrected");
        Assert.Equal("Sai số", audit.Reason);
        Assert.Contains("\"Ph\":7.4", audit.Before);
        Assert.Contains("\"Ph\":7.5", audit.After);
    }

    [Fact]
    public async Task Correction_into_violation_alerts_against_the_thresholds_of_the_measurement_not_todays()
    {
        var z = await api.ZoneAsync();
        var (_, manager) = await api.StaffAsync(z.Zone);
        var original = await z.RecordedAsync(8.0m, 20m);
        // Today's thresholds are wider; the correction must still use pH max 8.5 stored with the measurement.
        await api.UpdateAsync<FarmingZone>(z.Zone.Id, zone => zone.PhMax = 9.5m);

        var corrected = await (await z.Owner.CorrectAsync(original.Id(), 9.0m, 20m)).Content.ReadFromJsonAsync<JsonElement>();

        Assert.Equal("Active", corrected.Text("alertState"));
        Assert.Equal("pH 9.00 > 8.50", corrected.Text("violatedValues"));
        Assert.Equal(8.5m, corrected.GetProperty("thresholdsUsed").GetProperty("phMax").GetDecimal());
        // The Owner made the correction: a correction follows the same rule as a measurement (PM decision 03/11).
        var alerts = await api.AlertsOfAsync(corrected.Id());
        var alert = Assert.Single(alerts, n => n.RecipientAccountId == manager.Id);
        Assert.StartsWith($"Water alert (corrected measurement): zone {z.Zone.Name}, raft {z.Raft.Code}, ", alert.Body);
        Assert.DoesNotContain(alerts, n => n.RecipientAccountId == z.OwnerAccount.Id);
    }

    [Fact]
    public async Task Zone_manager_correcting_into_violation_alerts_the_owner_and_that_manager()
    {
        var z = await api.ZoneAsync();
        var (managerClient, manager) = await api.StaffAsync(z.Zone);
        var original = await z.RecordedAsync(8.0m, 20m);

        var corrected = await (await managerClient.CorrectAsync(original.Id(), 9.0m, 20m)).Content.ReadFromJsonAsync<JsonElement>();

        var alerts = await api.AlertsOfAsync(corrected.Id());
        Assert.Single(alerts, n => n.RecipientAccountId == z.OwnerAccount.Id);
        Assert.Single(alerts, n => n.RecipientAccountId == manager.Id);
    }

    [Fact]
    public async Task A_voided_record_is_final()
    {
        var z = await api.ZoneAsync();
        var saved = await z.RecordedAsync(7m, 20m);
        Assert.Equal(HttpStatusCode.OK, (await z.Owner.VoidAsync(saved.Id())).StatusCode);

        var voidAgain = await z.Owner.VoidAsync(saved.Id());
        var correctVoided = await z.Owner.CorrectAsync(saved.Id(), 7.1m, 20m);

        Assert.Equal("Water measurement is Voided and cannot be used for this action.",
            (await voidAgain.Content.ReadFromJsonAsync<JsonElement>()).Text("detail"));
        foreach (var response in new[] { voidAgain, correctVoided })
        {
            Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
            Assert.Equal("MSG-GEN-09", await response.ErrorCodeAsync());
        }
    }

    [Fact]
    public async Task Voiding_needs_a_reason_and_writes_one_audit_row()
    {
        var z = await api.ZoneAsync();
        var saved = await z.RecordedAsync(9.4m, 20m);

        var noReason = await z.Owner.VoidAsync(saved.Id(), reason: " ");
        var voided = await (await z.Owner.VoidAsync(saved.Id(), reason: "Đo nhầm vùng")).Content.ReadFromJsonAsync<JsonElement>();

        Assert.Equal(HttpStatusCode.BadRequest, noReason.StatusCode);
        Assert.Equal("Reason", (await noReason.Content.ReadFromJsonAsync<JsonElement>()).Text("field"));
        Assert.Equal(("Voided", "Voided", "Đo nhầm vùng"), (voided.Text("status"), voided.Text("alertState"), voided.Text("voidReason")));
        var audit = Assert.Single(await api.AuditOfAsync(saved.Id()), e => e.Action == "WaterMeasurement.Voided");
        Assert.Equal("Đo nhầm vùng", audit.Reason);
        Assert.Contains("ACTIVE", audit.Before);
        Assert.Contains("VOIDED", audit.After);
    }

    [Fact]
    public async Task Missing_reason_or_invalid_values_are_MSG01_and_change_nothing()
    {
        var z = await api.ZoneAsync();
        var saved = await z.RecordedAsync(7m, 20m);

        var noReason = await z.Owner.CorrectAsync(saved.Id(), 7.1m, 20m, reason: null);
        var tooLong = await z.Owner.CorrectAsync(saved.Id(), 7.1m, 20m, reason: new string('x', 1001));
        var badValue = await z.Owner.CorrectAsync(saved.Id(), 15m, 20m);
        var overSalinity = await z.Owner.CorrectAsync(saved.Id(), 7m, 50.5m);
        var noTemperature = await z.Owner.CorrectAsync(saved.Id(), 7m, 20m, temperature: null);

        Assert.Equal("Reason", (await noReason.Content.ReadFromJsonAsync<JsonElement>()).Text("field"));
        Assert.Equal("Reason", (await tooLong.Content.ReadFromJsonAsync<JsonElement>()).Text("field"));
        Assert.Equal("pH", (await badValue.Content.ReadFromJsonAsync<JsonElement>()).Text("field"));
        Assert.Equal("Salinity", (await overSalinity.Content.ReadFromJsonAsync<JsonElement>()).Text("field"));
        Assert.Equal("Temperature", (await noTemperature.Content.ReadFromJsonAsync<JsonElement>()).Text("field"));
        Assert.Equal(7m, await api.QueryAsync(db => db.WaterMeasurements.Where(m => m.Id == saved.Id()).Select(m => m.Ph).SingleAsync()));
    }

    [Fact]
    public async Task Other_editors_are_shown_with_MSG58_until_they_cancel()
    {
        var z = await api.ZoneAsync();
        var (manager, managerAccount) = await api.StaffAsync(z.Zone);
        var saved = await z.RecordedAsync(7m, 20m);
        var editing = $"/api/water-measurements/{saved.Id()}/editing";

        var started = await (await manager.PostAsync(editing, null)).Content.ReadFromJsonAsync<JsonElement>();
        var seenByOwner = await z.Owner.GetFromJsonAsync<JsonElement>($"/api/water-measurements/{saved.Id()}");
        var ownerStarts = await (await z.Owner.PostAsync(editing, null)).Content.ReadFromJsonAsync<JsonElement>();
        var cancelled = await manager.DeleteAsync(editing);
        var afterCancel = await z.Owner.GetFromJsonAsync<JsonElement>($"/api/water-measurements/{saved.Id()}");

        Assert.Empty(started.GetProperty("otherEditors").EnumerateArray());
        var editor = Assert.Single(seenByOwner.GetProperty("currentEditors").EnumerateArray());
        Assert.Equal(managerAccount.Id, editor.GetProperty("accountId").GetGuid());
        Assert.Equal("MSG58", seenByOwner.GetProperty("editingNotice").Text("code"));
        Assert.StartsWith($"{managerAccount.DisplayName} has been editing this record since ", seenByOwner.GetProperty("editingNotice").Text("text"));
        Assert.Equal("MSG58", ownerStarts.GetProperty("notice").Text("code"));
        Assert.Equal(HttpStatusCode.NoContent, cancelled.StatusCode);
        Assert.Empty(afterCancel.GetProperty("currentEditors").EnumerateArray());
        Assert.Equal(JsonValueKind.Null, afterCancel.GetProperty("editingNotice").ValueKind);
    }

    [Fact]
    public async Task Correcting_or_voiding_clears_the_callers_editing_indicator()
    {
        var z = await api.ZoneAsync();
        var (manager, _) = await api.StaffAsync(z.Zone);
        var saved = await z.RecordedAsync(7m, 20m);
        var editing = $"/api/water-measurements/{saved.Id()}/editing";

        await manager.PostAsync(editing, null);
        await manager.CorrectAsync(saved.Id(), 7.1m, 20m);
        var afterCorrection = await z.Owner.GetFromJsonAsync<JsonElement>($"/api/water-measurements/{saved.Id()}");
        await manager.PostAsync(editing, null);
        await manager.VoidAsync(saved.Id());
        var afterVoid = await z.Owner.GetFromJsonAsync<JsonElement>($"/api/water-measurements/{saved.Id()}");

        Assert.Empty(afterCorrection.GetProperty("currentEditors").EnumerateArray());
        Assert.Empty(afterVoid.GetProperty("currentEditors").EnumerateArray());
    }

    [Fact]
    public async Task Staff_without_water_rights_or_outside_the_zone_cannot_correct_void_or_edit()
    {
        var z = await api.ZoneAsync();
        var saved = await z.RecordedAsync(7m, 20m);
        // WATER-02 is No for the Accountant (§I.4.4).
        var (accountant, _) = await api.StaffAsync(z.Zone, StaffRole.Accountant);
        var (outsider, _) = await api.StaffAsync((await api.ZoneAsync()).Zone);

        foreach (var response in new[]
                 {
                     await accountant.CorrectAsync(saved.Id(), 7.1m, 20m),
                     await outsider.CorrectAsync(saved.Id(), 7.1m, 20m),
                     await outsider.VoidAsync(saved.Id()),
                     await outsider.PostAsync($"/api/water-measurements/{saved.Id()}/editing", null),
                 })
        {
            Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
            Assert.Equal("MSG-GEN-08", await response.ErrorCodeAsync());
        }

        Assert.Equal(StatusIds.WaterMeasurement.Active, await api.QueryAsync(db => db.WaterMeasurements.Where(m => m.Id == saved.Id()).Select(m => m.StatusId).SingleAsync()));
    }

    [Fact]
    public async Task Voided_measurement_leaves_nothing_current_in_history_chart_and_export()
    {
        var z = await api.ZoneAsync();
        var saved = await z.RecordedAsync(9.1m, 20m);
        await z.Owner.CorrectAsync(saved.Id(), 9.3m, 20m);

        var voided = await z.Owner.VoidAsync(saved.Id(), reason: "Đo nhầm cọc");
        var history = await z.Owner.GetFromJsonAsync<JsonElement>(z.Base);
        var chart = await z.Owner.GetFromJsonAsync<JsonElement>($"{z.Base}/chart");
        var export = await z.Owner.GetAsync($"{z.Base}/export");
        using var workbook = new ClosedXML.Excel.XLWorkbook(await export.Content.ReadAsStreamAsync());

        Assert.Equal(HttpStatusCode.OK, voided.StatusCode);
        Assert.Equal(0, history.GetProperty("total").GetInt32());
        Assert.All(chart.GetProperty("periods").EnumerateArray(), p => Assert.True(p.GetProperty("missing").GetBoolean()));
        Assert.Single(workbook.Worksheet("Measurements").RowsUsed());
    }

    [Fact]
    public async Task Unknown_measurement_is_404()
    {
        var z = await api.ZoneAsync();

        var response = await z.Owner.CorrectAsync(Guid.NewGuid(), 7m, 20m);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }
}
