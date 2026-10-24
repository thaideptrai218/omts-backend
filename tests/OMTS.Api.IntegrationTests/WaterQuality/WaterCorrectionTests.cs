using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using OMTS.Api.IntegrationTests.Infrastructure;
using OMTS.Domain.Constants;
using OMTS.Domain.Entities.Farming;

namespace OMTS.Api.IntegrationTests.WaterQuality;

/// <summary>ENV-02 – corrections (new version, alert re-evaluated with the thresholds used), voids, MSG06 and the editing indicator.</summary>
public class WaterCorrectionTests(AuthApiFactory api) : IClassFixture<AuthApiFactory>
{
    [Fact]
    public async Task Correcting_an_alerting_measurement_into_range_leaves_the_old_alert_corrected_and_raises_none()
    {
        var z = await api.ZoneAsync();
        var original = await z.RecordedAsync(9.1m, 20m);
        var alertsBefore = (await api.AlertsOfAsync(original.Id())).Count;

        var response = await z.Owner.CorrectAsync(original.Id(), 8.1m, 20m, recordVersion: 1, reason: "Đọc nhầm 8.1 thành 9.1");
        var corrected = await response.Content.ReadFromJsonAsync<JsonElement>();

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        Assert.Equal("Confirmed", corrected.Text("status"));
        Assert.Equal(2, corrected.GetProperty("recordVersion").GetInt32());
        Assert.Equal(original.Id(), corrected.GetProperty("correctedFromMeasurementId").GetGuid());
        Assert.Equal("None", corrected.Text("alertState"));
        Assert.Empty(await api.AlertsOfAsync(corrected.Id()));
        Assert.Equal(alertsBefore, (await api.AlertsOfAsync(original.Id())).Count);

        var old = await z.Owner.GetFromJsonAsync<JsonElement>($"/api/water-measurements/{original.Id()}");
        Assert.Equal("Corrected", old.Text("status"));
        Assert.Equal("Corrected", old.Text("alertState"));
        Assert.Equal(9.1m, old.GetProperty("ph").GetDecimal());
        Assert.False(old.GetProperty("isLatestVersion").GetBoolean());
        var versions = old.GetProperty("versions").EnumerateArray().ToList();
        Assert.Equal([1, 2], versions.Select(v => v.GetProperty("recordVersion").GetInt32()));
        Assert.Equal("Đọc nhầm 8.1 thành 9.1", versions[1].Text("correctionReason"));
    }

    [Fact]
    public async Task Correction_copies_the_evidence_and_is_audited_once_with_before_after_and_reason()
    {
        var z = await api.ZoneAsync();
        var recognized = await (await z.RecognizeAsync("ph7.4_sal21")).Content.ReadFromJsonAsync<JsonElement>();
        var draftId = recognized.GetProperty("measurement").Id();
        await z.Owner.PostAsJsonAsync($"/api/water-measurements/{draftId}/confirm", new { });

        var corrected = await (await z.Owner.CorrectAsync(draftId, 7.5m, 21.5m, recordVersion: 1, reason: "Sai số")).Content.ReadFromJsonAsync<JsonElement>();

        var stored = await api.QueryAsync(db => db.WaterMeasurements.AsNoTracking().Where(m => m.Id == draftId || m.Id == corrected.Id()).ToListAsync());
        var (old, next) = (stored.Single(m => m.Id == draftId), stored.Single(m => m.Id == corrected.Id()));
        Assert.Equal(old.PhotoUrl, next.PhotoUrl);
        Assert.Equal((7.4m, 21m), (next.OcrPh!.Value, next.OcrSalinity!.Value));
        Assert.Equal(old.MeasurementPoint, next.MeasurementPoint);
        Assert.Equal(StatusIds.WaterMeasurement.Superseded, old.StatusId);
        Assert.Equal(7.4m, old.Ph);

        var audit = Assert.Single(await api.AuditOfAsync(draftId), e => e.Action == "WaterMeasurement.Corrected");
        Assert.Equal("Sai số", audit.Reason);
        Assert.Contains("\"Status\":\"CONFIRMED\"", audit.Before);
        Assert.Contains(corrected.Id().ToString(), audit.After);
        Assert.Contains("7.5", audit.After);
    }

    [Fact]
    public async Task Correction_into_violation_raises_a_new_alert_against_the_thresholds_the_old_one_used()
    {
        var z = await api.ZoneAsync();
        var original = await z.RecordedAsync(8.0m, 20m);
        // Today's thresholds are wider; the correction must still use pH max 8.5 stored with the original.
        await api.UpdateAsync<FarmingZone>(z.Zone.Id, zone => zone.PhMax = 9.5m);

        var corrected = await (await z.Owner.CorrectAsync(original.Id(), 9.0m, 20m, recordVersion: 1)).Content.ReadFromJsonAsync<JsonElement>();

        Assert.Equal("Active", corrected.Text("alertState"));
        Assert.Equal("pH 9.00 > 8.50", corrected.Text("violatedValues"));
        Assert.Equal(8.5m, corrected.GetProperty("thresholdsUsed").GetProperty("phMax").GetDecimal());
        Assert.Single(await api.AlertsOfAsync(corrected.Id()), n => n.RecipientAccountId == z.OwnerAccount.Id);
    }

    [Fact]
    public async Task Second_user_correcting_the_same_version_gets_MSG06_with_the_first_users_version()
    {
        var z = await api.ZoneAsync();
        var (manager, managerAccount) = await api.StaffAsync(z.Zone, edit: true);
        var original = await z.RecordedAsync(7.0m, 20m);

        var first = await manager.CorrectAsync(original.Id(), 7.2m, 20m, recordVersion: 1);
        var second = await z.Owner.CorrectAsync(original.Id(), 7.9m, 20m, recordVersion: 1);
        var problem = await second.Content.ReadFromJsonAsync<JsonElement>();

        Assert.Equal(HttpStatusCode.Created, first.StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, second.StatusCode);
        Assert.Equal("MSG06", problem.Text("code"));
        Assert.StartsWith($"The data was just updated by {managerAccount.DisplayName} at ", problem.Text("detail"));
        var latest = problem.GetProperty("latestVersion");
        Assert.Equal((await first.Content.ReadFromJsonAsync<JsonElement>()).Id(), latest.Id());
        Assert.Equal(7.2m, latest.GetProperty("ph").GetDecimal());
        Assert.Equal(2, await api.QueryAsync(db => db.WaterMeasurements.CountAsync(m => m.ZoneId == z.Zone.Id)));
    }

    [Fact]
    public async Task Draft_cannot_be_corrected_and_final_records_cannot_be_corrected_or_voided_again()
    {
        var z = await api.ZoneAsync();
        var draft = await z.RecordedAsync(7m, 20m, confirm: false);
        var confirmed = await z.RecordedAsync(7m, 20m);
        var correctDraft = await z.Owner.CorrectAsync(draft.Id(), 7.1m, 20m, recordVersion: 1);
        Assert.Equal(HttpStatusCode.OK, (await z.Owner.VoidAsync(confirmed.Id(), 1)).StatusCode);

        var voidAgain = await z.Owner.VoidAsync(confirmed.Id(), 1);
        var correctVoided = await z.Owner.CorrectAsync(confirmed.Id(), 7.1m, 20m, recordVersion: 1);

        Assert.Equal("Cannot change water measurement from “Draft” to “Corrected”: only a confirmed measurement can be corrected.",
            (await correctDraft.Content.ReadFromJsonAsync<JsonElement>()).Text("detail"));
        Assert.Equal("Cannot change water measurement from “Voided” to “Voided”: the measurement has been voided.",
            (await voidAgain.Content.ReadFromJsonAsync<JsonElement>()).Text("detail"));
        foreach (var response in new[] { correctDraft, voidAgain, correctVoided })
        {
            Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
            Assert.Equal("MSG61", await response.ErrorCodeAsync());
        }
    }

    [Fact]
    public async Task Corrected_version_cannot_be_voided_or_corrected_once_its_successor_is_known()
    {
        var z = await api.ZoneAsync();
        var original = await z.RecordedAsync(7m, 20m);
        await z.Owner.CorrectAsync(original.Id(), 7.3m, 20m, recordVersion: 1);

        var voidOld = await z.Owner.VoidAsync(original.Id(), recordVersion: 2);
        var correctOld = await z.Owner.CorrectAsync(original.Id(), 7.4m, 20m, recordVersion: 2);

        Assert.Equal("MSG61", await voidOld.ErrorCodeAsync());
        Assert.Equal("MSG61", await correctOld.ErrorCodeAsync());
        Assert.Contains("“Corrected”", (await correctOld.Content.ReadFromJsonAsync<JsonElement>()).Text("detail"));
    }

    [Fact]
    public async Task Voiding_a_draft_or_the_latest_version_needs_a_reason_and_writes_one_audit_row()
    {
        var z = await api.ZoneAsync();
        var draft = await z.RecordedAsync(7m, 20m, confirm: false);
        var confirmed = await z.RecordedAsync(9.4m, 20m);

        var noReason = await z.Owner.VoidAsync(confirmed.Id(), 1, reason: " ");
        var voidedDraft = await z.Owner.VoidAsync(draft.Id(), 1);
        var voided = await (await z.Owner.VoidAsync(confirmed.Id(), 1, reason: "Đo nhầm vùng")).Content.ReadFromJsonAsync<JsonElement>();

        Assert.Equal(HttpStatusCode.BadRequest, noReason.StatusCode);
        Assert.Equal("Reason", (await noReason.Content.ReadFromJsonAsync<JsonElement>()).Text("field"));
        Assert.Equal(HttpStatusCode.OK, voidedDraft.StatusCode);
        Assert.Equal("Voided", voided.Text("status"));
        Assert.Equal("Voided", voided.Text("alertState"));
        Assert.Equal("Đo nhầm vùng", voided.Text("voidReason"));
        var audit = Assert.Single(await api.AuditOfAsync(confirmed.Id()), e => e.Action == "WaterMeasurement.Voided");
        Assert.Equal("Đo nhầm vùng", audit.Reason);
        Assert.Contains("CONFIRMED", audit.Before);
        Assert.Contains("VOIDED", audit.After);
        Assert.Single(await api.AuditOfAsync(draft.Id()), e => e.Action == "WaterMeasurement.Voided");
    }

    [Fact]
    public async Task Missing_reason_record_version_or_invalid_values_are_MSG01()
    {
        var z = await api.ZoneAsync();
        var confirmed = await z.RecordedAsync(7m, 20m);

        var noReason = await z.Owner.CorrectAsync(confirmed.Id(), 7.1m, 20m, recordVersion: 1, reason: null);
        var tooLong = await z.Owner.CorrectAsync(confirmed.Id(), 7.1m, 20m, recordVersion: 1, reason: new string('x', 1001));
        var badValue = await z.Owner.CorrectAsync(confirmed.Id(), 15m, 20m, recordVersion: 1);
        var noVersion = await z.Owner.PostAsJsonAsync($"/api/water-measurements/{confirmed.Id()}/corrections",
            new { ph = 7.1m, salinity = 20m, measuredAt = DateTimeOffset.UtcNow, reason = "x" });

        Assert.Equal("Reason", (await noReason.Content.ReadFromJsonAsync<JsonElement>()).Text("field"));
        Assert.Equal("Reason", (await tooLong.Content.ReadFromJsonAsync<JsonElement>()).Text("field"));
        Assert.Equal("pH", (await badValue.Content.ReadFromJsonAsync<JsonElement>()).Text("field"));
        Assert.Equal("Record version", (await noVersion.Content.ReadFromJsonAsync<JsonElement>()).Text("field"));
        Assert.Equal(1, await api.QueryAsync(db => db.WaterMeasurements.CountAsync(m => m.ZoneId == z.Zone.Id)));
    }

    [Fact]
    public async Task Other_editors_are_shown_with_MSG58_until_they_cancel()
    {
        var z = await api.ZoneAsync();
        var (manager, managerAccount) = await api.StaffAsync(z.Zone, edit: true);
        var confirmed = await z.RecordedAsync(7m, 20m);
        var editing = $"/api/water-measurements/{confirmed.Id()}/editing";

        var started = await (await manager.PostAsync(editing, null)).Content.ReadFromJsonAsync<JsonElement>();
        var seenByOwner = await z.Owner.GetFromJsonAsync<JsonElement>($"/api/water-measurements/{confirmed.Id()}");
        var ownerStarts = await (await z.Owner.PostAsync(editing, null)).Content.ReadFromJsonAsync<JsonElement>();
        var cancelled = await manager.DeleteAsync(editing);
        var afterCancel = await z.Owner.GetFromJsonAsync<JsonElement>($"/api/water-measurements/{confirmed.Id()}");

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
    public async Task Saving_clears_the_savers_editing_indicator()
    {
        var z = await api.ZoneAsync();
        var (manager, _) = await api.StaffAsync(z.Zone, edit: true);
        var confirmed = await z.RecordedAsync(7m, 20m);
        await manager.PostAsync($"/api/water-measurements/{confirmed.Id()}/editing", null);

        await manager.CorrectAsync(confirmed.Id(), 7.1m, 20m, recordVersion: 1);

        var old = await z.Owner.GetFromJsonAsync<JsonElement>($"/api/water-measurements/{confirmed.Id()}");
        Assert.Empty(old.GetProperty("currentEditors").EnumerateArray());
    }

    [Fact]
    public async Task Staff_without_edit_or_outside_the_zone_cannot_correct_void_or_edit()
    {
        var z = await api.ZoneAsync();
        var confirmed = await z.RecordedAsync(7m, 20m);
        var (viewer, _) = await api.StaffAsync(z.Zone, create: true);
        var (outsider, _) = await api.StaffAsync((await api.ZoneAsync()).Zone, edit: true);

        var responses = new[]
        {
            await viewer.CorrectAsync(confirmed.Id(), 7.1m, 20m, recordVersion: 1),
            await outsider.CorrectAsync(confirmed.Id(), 7.1m, 20m, recordVersion: 1),
            await outsider.VoidAsync(confirmed.Id(), 1),
            await outsider.PostAsync($"/api/water-measurements/{confirmed.Id()}/editing", null),
        };

        foreach (var response in responses)
        {
            Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
            Assert.Equal("MSG02", await response.ErrorCodeAsync());
        }

        Assert.Equal(StatusIds.WaterMeasurement.Confirmed, await api.QueryAsync(db => db.WaterMeasurements.Where(m => m.Id == confirmed.Id()).Select(m => m.StatusId).SingleAsync()));
    }

    [Fact]
    public async Task Unknown_measurement_is_404()
    {
        var z = await api.ZoneAsync();

        var response = await z.Owner.CorrectAsync(Guid.NewGuid(), 7m, 20m, recordVersion: 1);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }
}
