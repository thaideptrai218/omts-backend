using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using OMTS.Api.IntegrationTests.Infrastructure;
using OMTS.Domain.Constants;
using OMTS.Domain.Entities.Farming;

namespace OMTS.Api.IntegrationTests.WaterQuality;

/// <summary>
/// WATER-01 and AC-04: recognize saves nothing, the record request saves the values the user confirmed as Active with a
/// copy of the zone thresholds; the values read are only suggestions and a threshold change never touches the copy.
/// </summary>
public class WaterRecordingTests(AuthApiFactory api) : IClassFixture<AuthApiFactory>
{
    // AC-04: OCR reads 21 ‰ from a photo showing 12 ‰; nothing is saved or alerted before the Zone Manager confirms 12 ‰.
    // With thresholds 15–30 ‰ the alert goes out; the Owner then changes the minimum to 10 ‰ and the measurement keeps 15–30 ‰.
    [Fact]
    public async Task Ac_04_nothing_is_saved_before_the_confirmation_and_a_threshold_change_keeps_the_copy_of_the_measurement()
    {
        var z = await api.ZoneAsync();
        var (manager, managerAccount) = await api.StaffAsync(z.Zone);

        var recognized = await manager.RecognizeAsync(z.Zone.Id, "ph7.8_sal21_tmp27.5");
        var body = await recognized.Content.ReadFromJsonAsync<JsonElement>();

        Assert.Equal(HttpStatusCode.OK, recognized.StatusCode);
        Assert.Equal(21m, body.GetProperty("ocr").GetProperty("salinity").GetProperty("value").GetDecimal());
        Assert.Equal(27.5m, body.GetProperty("ocr").GetProperty("temperature").GetProperty("value").GetDecimal());
        Assert.Equal(0, await api.QueryAsync(db => db.WaterMeasurements.CountAsync(m => m.ZoneId == z.Zone.Id)));
        Assert.Equal(0, await api.QueryAsync(db => db.AuditEvents.CountAsync(e => e.ZoneId == z.Zone.Id && e.RecordType == "WaterMeasurement")));
        Assert.Equal(0, await NotificationsOfAsync(managerAccount.Id));

        var recorded = await z.RecordAsync(7.8m, 12m, 27.5m, client: manager, photoKey: body.Text("photoKey"));
        var saved = await recorded.Content.ReadFromJsonAsync<JsonElement>();

        Assert.Equal(HttpStatusCode.Created, recorded.StatusCode);
        Assert.Equal(("Active", "ACTIVE", 12m), (saved.Text("status"), saved.Text("statusCode"), saved.GetProperty("salinity").GetDecimal()));
        Assert.Equal("Active", saved.Text("alertState"));
        Assert.Equal("Salinity 12.00 < 15.00", saved.Text("violatedValues"));
        var alerts = await api.AlertsOfAsync(saved.Id());
        Assert.Single(alerts, n => n.RecipientAccountId == z.OwnerAccount.Id);
        Assert.Single(alerts, n => n.RecipientAccountId == managerAccount.Id);
        Assert.All(alerts, n => Assert.Contains("Salinity 12 ‰ is too low (allowed 15–30 ‰)", n.Body));

        var changed = await z.Owner.PutAsJsonAsync($"/api/zones/{z.Zone.Id}/water-thresholds", new
        {
            phMin = 6.5m, phMax = 8.5m, salinityMin = 10m, salinityMax = 30m, temperatureMin = 10m, temperatureMax = 35m,
        });
        var after = await z.Owner.GetFromJsonAsync<JsonElement>($"/api/water-measurements/{saved.Id()}");

        Assert.Equal(HttpStatusCode.OK, changed.StatusCode);
        Assert.Equal((15m, 30m), (after.GetProperty("thresholdsUsed").GetProperty("salinityMin").GetDecimal(), after.GetProperty("thresholdsUsed").GetProperty("salinityMax").GetDecimal()));
        Assert.Equal("Active", after.Text("alertState"));
        Assert.Equal("None", (await z.RecordedAsync(7.8m, 12m, client: manager)).Text("alertState"));
    }

    [Fact]
    public async Task Recognize_answers_the_suggestions_and_the_values_outside_the_zone_thresholds_with_msg_water_02()
    {
        var z = await api.ZoneAsync();
        var before = DateTimeOffset.UtcNow;

        var body = await (await z.RecognizeAsync("ph9.1_sal12_tmp28 conf0.7")).Content.ReadFromJsonAsync<JsonElement>();

        var ocr = body.GetProperty("ocr");
        Assert.Equal("Success", ocr.Text("status"));
        Assert.Equal(9.1m, ocr.GetProperty("ph").GetProperty("value").GetDecimal());
        Assert.True(ocr.GetProperty("ph").GetProperty("uncertain").GetBoolean());
        Assert.Equal(0.7, ocr.GetProperty("salinity").GetProperty("confidence").GetDouble());
        Assert.Empty(ocr.GetProperty("unread").EnumerateArray());
        Assert.Equal(JsonValueKind.Null, ocr.GetProperty("warning").ValueKind);
        Assert.StartsWith($"water-meter-photos/{z.OwnerAccount.Id:N}/", body.Text("photoKey"));
        Assert.InRange(body.GetProperty("measuredAt").GetDateTimeOffset(), before.AddSeconds(-1), DateTimeOffset.UtcNow.AddSeconds(1));
        Assert.Equal(["PH", "SALINITY"], body.GetProperty("valuesOutside").EnumerateArray().Select(v => v.Text("indicator")));
        Assert.Equal("MSG-WATER-02", body.GetProperty("thresholdWarning").Text("code"));
        Assert.Equal("pH 9.1 is too high (allowed 6.5–8.5); Salinity 12 ‰ is too low (allowed 15–30 ‰). Please check the meter again before you save.", body.GetProperty("thresholdWarning").Text("text"));
    }

    [Fact]
    public async Task Values_read_inside_the_thresholds_have_no_threshold_warning()
    {
        var z = await api.ZoneAsync();

        var body = await (await z.RecognizeAsync("ph7.1_sal20_tmp25")).Content.ReadFromJsonAsync<JsonElement>();

        Assert.Empty(body.GetProperty("valuesOutside").EnumerateArray());
        Assert.Equal(JsonValueKind.Null, body.GetProperty("thresholdWarning").ValueKind);
    }

    [Theory]
    [InlineData("ocr-fail")]
    [InlineData("")]
    public async Task Ocr_failure_answers_the_key_and_msg_water_01_for_every_value_and_saves_nothing(string marker)
    {
        // "" with the Fail mode configured on the host; the marker forces it on this host.
        await using var failing = new WaterApiFactory(new Dictionary<string, string> { ["Ocr:Fake:Mode"] = "Fail" });
        var factory = marker == "" ? failing : api;
        var z = await factory.ZoneAsync();

        var response = await z.RecognizeAsync(marker);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var ocr = body.GetProperty("ocr");
        Assert.Equal("Failed", ocr.Text("status"));
        Assert.Equal(["PH", "SALINITY", "TEMPERATURE"], ocr.GetProperty("unread").EnumerateArray().Select(v => v.GetString()));
        Assert.Equal("MSG-WATER-01", ocr.GetProperty("warning").Text("code"));
        Assert.Equal(0, await factory.QueryAsync(db => db.WaterMeasurements.CountAsync(m => m.ZoneId == z.Zone.Id)));
        Assert.Equal(HttpStatusCode.Created, (await z.RecordAsync(7m, 20m, photoKey: body.Text("photoKey"))).StatusCode);
    }

    [Fact]
    public async Task Ocr_timeout_is_a_failed_recognition_not_an_error()
    {
        await using var slow = new WaterApiFactory(new Dictionary<string, string> { ["Ocr:TimeoutSeconds"] = "1" });
        var z = await slow.ZoneAsync();

        var response = await z.RecognizeAsync("ocr-timeout");
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("Failed", body.GetProperty("ocr").Text("status"));
        Assert.NotNull(body.Text("photoKey"));
        Assert.Equal(0, await slow.QueryAsync(db => db.WaterMeasurements.CountAsync(m => m.ZoneId == z.Zone.Id)));
    }

    [Fact]
    public async Task Zone_manager_records_in_the_granted_zone_with_a_copy_of_every_threshold_and_is_audited_once()
    {
        var z = await api.ZoneAsync();
        var (manager, account) = await api.StaffAsync(z.Zone);

        var body = await z.RecordedAsync(7.2m, 21m, client: manager, temperature: 26.5m);

        Assert.Equal(account.Id, body.GetProperty("measuredBy").GetProperty("id").GetGuid());
        Assert.Equal((z.Raft.Id, z.Raft.Code), (body.GetProperty("raftId").GetGuid(), body.Text("raftCode")));
        Assert.Equal(26.5m, body.GetProperty("temperature").GetDecimal());
        Assert.NotNull(body.Text("photoUrl"));
        var stored = await api.QueryAsync(db => db.WaterMeasurements.AsNoTracking().SingleAsync(m => m.Id == body.Id()));
        Assert.Equal(StatusIds.WaterMeasurement.Active, stored.StatusId);
        Assert.Equal((6.5m, 8.5m, 15m, 30m, 10m, 35m), (stored.PhMinUsed!.Value, stored.PhMaxUsed!.Value, stored.SalinityMinUsed!.Value, stored.SalinityMaxUsed!.Value, stored.TemperatureMinUsed!.Value, stored.TemperatureMaxUsed!.Value));
        var audit = Assert.Single(await api.AuditOfAsync(body.Id()));
        Assert.Equal("WaterMeasurement.Created", audit.Action);
        Assert.Contains("\"Temperature\":26.5", audit.After);
    }

    [Fact]
    public async Task Values_equal_to_the_bounds_raise_no_alert_and_unset_bounds_are_not_checked()
    {
        var z = await api.ZoneAsync();
        var atMin = await z.RecordedAsync(6.5m, 15m, temperature: 10m);
        var atMax = await z.RecordedAsync(8.5m, 30m, temperature: 35m);
        await api.UpdateAsync<FarmingZone>(z.Zone.Id, zone =>
        {
            (zone.PhMin, zone.PhMax, zone.SalinityMin, zone.SalinityMax) = (null, 8.5m, null, null);
            (zone.TemperatureMin, zone.TemperatureMax) = (null, null);
        });
        var onlyPhMax = await z.RecordedAsync(1m, 49m, temperature: 45m);

        foreach (var body in new[] { atMin, atMax, onlyPhMax })
        {
            Assert.Equal("None", body.Text("alertState"));
            Assert.False(body.GetProperty("thresholdExceeded").GetBoolean());
            Assert.Empty(await api.AlertsOfAsync(body.Id()));
        }

        Assert.Equal(JsonValueKind.Null, onlyPhMax.GetProperty("thresholdsUsed").GetProperty("phMin").ValueKind);
    }

    [Fact]
    public async Task Record_of_another_zone_cannot_be_read_by_id_and_the_old_steps_are_gone()
    {
        var z = await api.ZoneAsync();
        var saved = await z.RecordedAsync(7m, 20m);
        var (otherZoneManager, _) = await api.StaffAsync((await api.ZoneAsync()).Zone);

        var read = await otherZoneManager.GetAsync($"/api/water-measurements/{saved.Id()}");
        var confirm = await z.Owner.PostAsJsonAsync($"/api/water-measurements/{saved.Id()}/confirm", new { });
        var again = await z.Owner.PostAsync($"/api/water-measurements/{saved.Id()}/recognize", null);
        var withPhoto = await z.Owner.PostAsync($"{z.Base}/with-photo", WaterTestKit.PhotoForm(WaterTestKit.Photo()));

        Assert.Equal(HttpStatusCode.Forbidden, read.StatusCode);
        Assert.Equal("MSG-GEN-08", await read.ErrorCodeAsync());
        Assert.Equal(HttpStatusCode.NotFound, confirm.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, again.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, withPhoto.StatusCode);
    }

    // Done when: the recognize → record flow is usable from Swagger.
    [Fact]
    public async Task Swagger_document_describes_the_water_endpoints()
    {
        var response = await api.CreateClient().GetAsync("/swagger/v1/swagger.json");
        var paths = (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("paths");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        foreach (var path in new[]
        {
            "/api/zones/{zoneId}/water-measurements/recognize", "/api/zones/{zoneId}/water-measurements",
            "/api/water-measurements/{id}/corrections", "/api/zones/{zoneId}/water-thresholds",
        })
        {
            Assert.True(paths.TryGetProperty(path, out _), path);
        }

        Assert.False(paths.TryGetProperty("/api/zones/{zoneId}/water-measurements/with-photo", out _));
        Assert.False(paths.TryGetProperty("/api/water-measurements/{id}/confirm", out _));
    }

    private Task<int> NotificationsOfAsync(Guid accountId) =>
        api.QueryAsync(db => db.Notifications.CountAsync(n => n.RecipientAccountId == accountId));
}
