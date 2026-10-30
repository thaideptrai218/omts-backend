using System.Net.Http.Json;
using System.Net;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using OMTS.Api.IntegrationTests.Infrastructure;
using OMTS.Domain.Constants;
using OMTS.Domain.Entities.Cultivation;
using OMTS.Domain.Entities.Farming;
using OMTS.Domain.Enums;
using OMTS.Infrastructure.Messaging.Fakes;
using static OMTS.Api.IntegrationTests.Infrastructure.AcsTestKit;

namespace OMTS.Api.IntegrationTests.WaterQuality;

/// <summary>ENV-01 – photo → OCR draft → confirm, manual entry / offline sync, alerts (MSG27) and idempotent re-sends.</summary>
public class WaterRecordingTests(AuthApiFactory api) : IClassFixture<AuthApiFactory>
{
    [Fact]
    public async Task Photo_is_read_as_a_draft_then_confirmed_with_the_final_values()
    {
        var z = await api.ZoneAsync();

        var recognized = await z.RecognizeAsync("ph7.8_sal25.5 conf0.7 at2026-10-20T08:15");
        var body = await recognized.Content.ReadFromJsonAsync<JsonElement>();
        var draft = body.GetProperty("measurement");
        var ocr = body.GetProperty("ocr");

        Assert.Equal(HttpStatusCode.OK, recognized.StatusCode);
        Assert.Equal("Success", ocr.Text("status"));
        Assert.Equal(7.8m, ocr.GetProperty("ph").GetProperty("value").GetDecimal());
        Assert.True(ocr.GetProperty("ph").GetProperty("uncertain").GetBoolean());
        Assert.Equal(0.7, ocr.GetProperty("salinity").GetProperty("confidence").GetDouble());
        Assert.Equal("Draft", draft.Text("status"));
        Assert.Equal("Ocr", draft.Text("inputSource"));
        Assert.Equal("Cọc đo số 1", draft.Text("measurementPoint"));
        Assert.Equal(new DateTimeOffset(2026, 10, 20, 8, 15, 0, TimeSpan.FromHours(7)), draft.GetProperty("measuredAt").GetDateTimeOffset());
        Assert.NotNull(draft.Text("photoUrl"));
        Assert.Equal(25.5m, draft.GetProperty("ocrDraft").GetProperty("salinity").GetDecimal());

        var confirmed = await z.Owner.PostAsJsonAsync($"/api/water-measurements/{draft.Id()}/confirm", new { ph = 7.6m });
        var final = await confirmed.Content.ReadFromJsonAsync<JsonElement>();

        Assert.Equal(HttpStatusCode.OK, confirmed.StatusCode);
        Assert.Equal("Confirmed", final.Text("status"));
        Assert.Equal(7.6m, final.GetProperty("ph").GetDecimal());
        Assert.Equal(25.5m, final.GetProperty("salinity").GetDecimal());
        // The first OCR draft is kept unchanged next to the confirmed values.
        Assert.Equal(7.8m, final.GetProperty("ocrDraft").GetProperty("ph").GetDecimal());
        Assert.Equal(8.5m, final.GetProperty("thresholdsUsed").GetProperty("phMax").GetDecimal());
        Assert.Equal(z.OwnerAccount.Id, final.GetProperty("confirmedBy").GetProperty("id").GetGuid());
        var actions = (await api.AuditOfAsync(draft.Id())).Select(e => e.Action);
        Assert.Equal(["WaterMeasurement.Created", "WaterMeasurement.Confirmed"], actions);
    }

    [Theory]
    [InlineData("ocr-fail")]
    [InlineData("")]
    public async Task Ocr_failure_still_saves_the_draft_with_the_photo_and_asks_for_manual_entry(string marker)
    {
        // "" with the Fail mode configured on the host; the marker forces it on this host.
        await using var failing = new WaterApiFactory(new Dictionary<string, string> { ["Ocr:Fake:Mode"] = "Fail" });
        var z = await (marker == "" ? failing : api).ZoneAsync();

        var response = await z.RecognizeAsync(marker);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("Failed", body.GetProperty("ocr").Text("status"));
        Assert.Equal("MSG04", body.GetProperty("ocr").GetProperty("warning").Text("code"));
        var draft = body.GetProperty("measurement");
        Assert.Equal("Draft", draft.Text("status"));
        Assert.Equal("Manual", draft.Text("inputSource"));
        Assert.Equal(JsonValueKind.Null, draft.GetProperty("ph").ValueKind);
        Assert.Equal(JsonValueKind.Null, draft.GetProperty("ocrDraft").GetProperty("ph").ValueKind);
        Assert.NotNull(draft.Text("photoUrl"));
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
        Assert.Equal(1, await slow.QueryAsync(db => db.WaterMeasurements.CountAsync(m => m.ZoneId == z.Zone.Id)));
    }

    [Fact]
    public async Task Draft_kept_for_recognition_once_online_can_be_sent_to_ocr_again()
    {
        var z = await api.ZoneAsync();
        var draftId = (await (await z.RecognizeAsync("ocr-fail ph8.1_sal22")).Content.ReadFromJsonAsync<JsonElement>()).GetProperty("measurement").Id();
        // The stored photo still carries the failure marker: drop it so the fake reads the values.
        await ReplaceStoredPhotoAsync(api, draftId, WaterTestKit.Photo("ph8.1_sal22"));

        var again = await z.Owner.PostAsync($"/api/water-measurements/{draftId}/recognize", null);
        var body = await again.Content.ReadFromJsonAsync<JsonElement>();
        var once = await z.Owner.PostAsync($"/api/water-measurements/{draftId}/recognize", null);

        Assert.Equal(HttpStatusCode.OK, again.StatusCode);
        Assert.Equal("Success", body.GetProperty("ocr").Text("status"));
        Assert.Equal(8.1m, body.GetProperty("measurement").GetProperty("ph").GetDecimal());
        Assert.Equal("Ocr", body.GetProperty("measurement").Text("inputSource"));
        Assert.Equal("NotCalled", (await once.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("ocr").Text("status"));
    }

    [Fact]
    public async Task Photo_missing_not_jpeg_or_png_or_over_10_mb_is_refused_and_nothing_is_saved()
    {
        var z = await api.ZoneAsync();
        var missing = await z.Owner.PostAsync($"{z.Base}/recognize", new MultipartFormDataContent { { new StringContent(Guid.NewGuid().ToString()), "clientOperationId" } });
        var pdf = await z.Owner.PostAsync($"{z.Base}/recognize", WaterTestKit.PhotoForm("%PDF-1.7 renamed"u8.ToArray(), contentType: "image/png"));
        var huge = await z.Owner.PostAsync($"{z.Base}/recognize", WaterTestKit.PhotoForm([.. WaterTestKit.PngSignature, .. new byte[10 * 1024 * 1024]]));

        foreach (var response in new[] { missing, pdf, huge })
        {
            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
            Assert.Equal("MSG01", await response.ErrorCodeAsync());
            Assert.Equal("Photo", (await response.Content.ReadFromJsonAsync<JsonElement>()).Text("field"));
        }

        Assert.Equal(0, await api.QueryAsync(db => db.WaterMeasurements.CountAsync(m => m.ZoneId == z.Zone.Id)));
    }

    [Fact]
    public async Task Photo_storage_failure_answers_502_MSG05_and_saves_nothing()
    {
        await using var broken = new WaterApiFactory(services: s => s.AddSingleton<OMTS.Application.Common.Interfaces.Storage.IFileStorage, FailingFileStorage>());
        var z = await broken.ZoneAsync();

        var recognized = await z.RecognizeAsync("ph7_sal20");
        var manual = await z.Owner.PostAsync($"{z.Base}/with-photo", ManualForm(Guid.NewGuid(), WaterTestKit.Photo()));

        Assert.Equal(HttpStatusCode.BadGateway, recognized.StatusCode);
        Assert.Equal("MSG05", await recognized.ErrorCodeAsync());
        Assert.Equal(HttpStatusCode.BadGateway, manual.StatusCode);
        Assert.Equal(0, await broken.QueryAsync(db => db.WaterMeasurements.CountAsync()));
    }

    [Fact]
    public async Task Confirmed_ph_above_max_alerts_each_recipient_exactly_once_even_when_sent_twice()
    {
        var z = await api.ZoneAsync();
        var (_, manager) = await api.StaffAsync(z.Zone, create: true);
        var (_, otherZoneManager) = await api.StaffAsync((await api.ZoneAsync()).Zone, create: true);
        var operationId = Guid.NewGuid();

        var first = await z.RecordAsync(9.1m, 20m, clientOperationId: operationId);
        var again = await z.RecordAsync(9.1m, 20m, clientOperationId: operationId);
        var body = await first.Content.ReadFromJsonAsync<JsonElement>();

        Assert.Equal(HttpStatusCode.Created, first.StatusCode);
        Assert.Equal(HttpStatusCode.OK, again.StatusCode);
        Assert.Equal(body.Id(), (await again.Content.ReadFromJsonAsync<JsonElement>()).Id());
        Assert.Equal("Active", body.Text("alertState"));
        Assert.Equal("pH 9.10 > 8.50", body.Text("violatedValues"));
        Assert.Equal(1, await api.QueryAsync(db => db.WaterMeasurements.CountAsync(m => m.ZoneId == z.Zone.Id)));

        var alerts = await api.AlertsOfAsync(body.Id());
        Assert.Single(alerts, n => n.RecipientAccountId == z.OwnerAccount.Id);
        Assert.Single(alerts, n => n.RecipientAccountId == manager.Id);
        Assert.DoesNotContain(alerts, n => n.RecipientAccountId == otherZoneManager.Id);
        Assert.All(alerts, n => Assert.True(n.IsMandatory));
        Assert.All(alerts, n => Assert.Equal($"pH at zone {z.Zone.Name} exceeds the set threshold.", n.Body));
        var pushed = api.Services.GetRequiredService<FakePushSender>().Inbox.List(manager.Id.ToString());
        Assert.Single(pushed, m => m.Text.Contains($"pH at zone {z.Zone.Name} exceeds the set threshold."));
    }

    [Fact]
    public async Task Both_indicators_out_of_range_are_named_in_one_alert()
    {
        var z = await api.ZoneAsync();

        var body = await z.RecordedAsync(5.9m, 31.2m);

        Assert.Equal("pH 5.90 < 6.50; Salinity 31.20 > 30.00", body.Text("violatedValues"));
        Assert.Single(await api.AlertsOfAsync(body.Id()), n => n.RecipientAccountId == z.OwnerAccount.Id && n.Body == $"pH, Salinity at zone {z.Zone.Name} exceeds the set threshold.");
    }

    [Fact]
    public async Task Values_equal_to_the_bounds_raise_no_alert_and_unset_bounds_are_not_checked()
    {
        var z = await api.ZoneAsync();
        var atMin = await z.RecordedAsync(6.5m, 15m);
        var atMax = await z.RecordedAsync(8.5m, 30m);
        await api.UpdateAsync<FarmingZone>(z.Zone.Id, zone => (zone.PhMin, zone.PhMax, zone.SalinityMin, zone.SalinityMax) = (null, 8.5m, null, null));
        var onlyPhMax = await z.RecordedAsync(1m, 99m);

        foreach (var body in new[] { atMin, atMax, onlyPhMax })
        {
            Assert.Equal("None", body.Text("alertState"));
            Assert.False(body.GetProperty("thresholdExceeded").GetBoolean());
            Assert.Empty(await api.AlertsOfAsync(body.Id()));
        }

        Assert.Equal(JsonValueKind.Null, onlyPhMax.GetProperty("thresholdsUsed").GetProperty("phMin").ValueKind);
    }

    [Fact]
    public async Task Draft_never_raises_an_alert_and_is_left_out_of_history_and_chart()
    {
        var z = await api.ZoneAsync();
        var draft = await z.RecordedAsync(12m, 90m, confirm: false);

        var history = await z.Owner.GetFromJsonAsync<JsonElement>(z.Base);
        var chart = await z.Owner.GetFromJsonAsync<JsonElement>($"{z.Base}/chart");

        Assert.Equal("Draft", draft.Text("status"));
        Assert.Equal("None", draft.Text("alertState"));
        Assert.Equal(JsonValueKind.Null, draft.GetProperty("thresholdsUsed").ValueKind);
        Assert.Empty(await api.AlertsOfAsync(draft.Id()));
        Assert.Equal(0, history.GetProperty("total").GetInt32());
        Assert.All(chart.GetProperty("periods").EnumerateArray(), p => Assert.True(p.GetProperty("missing").GetBoolean()));
    }

    [Fact]
    public async Task Confirming_a_record_that_is_not_a_draft_is_MSG61()
    {
        var z = await api.ZoneAsync();
        var confirmed = await z.RecordedAsync(7m, 20m);

        var response = await z.Owner.PostAsJsonAsync($"/api/water-measurements/{confirmed.Id()}/confirm", new { });
        var problem = await response.Content.ReadFromJsonAsync<JsonElement>();

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal("MSG61", problem.Text("code"));
        Assert.Equal("Cannot change water measurement from “Confirmed” to “Confirmed”: only a Draft can be confirmed.", problem.Text("detail"));
    }

    [Theory]
    [InlineData(14.01, 20.0, "pH")]
    [InlineData(-0.5, 20.0, "pH")]
    [InlineData(7.0, 100.01, "Salinity")]
    [InlineData(null, 20.0, "pH")]
    public async Task Invalid_or_missing_values_are_MSG01_and_nothing_is_saved(double? ph, double salinity, string field)
    {
        var z = await api.ZoneAsync();

        var response = await z.RecordAsync((decimal?)ph, (decimal)salinity);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("MSG01", await response.ErrorCodeAsync());
        Assert.Equal($"{field} is invalid. Please check and enter it again.", (await response.Content.ReadFromJsonAsync<JsonElement>()).Text("detail"));
        Assert.Equal(0, await api.QueryAsync(db => db.WaterMeasurements.CountAsync(m => m.ZoneId == z.Zone.Id)));
    }

    [Fact]
    public async Task Future_time_missing_operation_id_and_discontinued_zone_are_MSG01()
    {
        var z = await api.ZoneAsync();
        var future = await z.RecordAsync(7m, 20m, measuredAt: DateTimeOffset.UtcNow.AddMinutes(10));
        var noOperation = await z.Owner.PostAsJsonAsync(z.Base, new { ph = 7m, salinity = 20m, measuredAt = DateTimeOffset.UtcNow, confirm = true });
        await api.UpdateAsync<FarmingZone>(z.Zone.Id, zone => zone.StatusId = StatusIds.Usage.Discontinued);
        var discontinued = await z.RecordAsync(7m, 20m);
        var recognizeDiscontinued = await z.RecognizeAsync();

        Assert.Equal("Measured at", (await future.Content.ReadFromJsonAsync<JsonElement>()).Text("field"));
        Assert.Equal("Client operation id", (await noOperation.Content.ReadFromJsonAsync<JsonElement>()).Text("field"));
        Assert.Equal("Zone", (await discontinued.Content.ReadFromJsonAsync<JsonElement>()).Text("field"));
        Assert.Equal(HttpStatusCode.BadRequest, recognizeDiscontinued.StatusCode);
        Assert.Equal(0, await api.QueryAsync(db => db.WaterMeasurements.CountAsync(m => m.ZoneId == z.Zone.Id)));
    }

    [Fact]
    public async Task Multipart_manual_entry_with_a_photo_reads_numbers_invariantly()
    {
        var z = await api.ZoneAsync();

        var response = await z.Owner.PostAsync($"{z.Base}/with-photo", ManualForm(Guid.NewGuid(), WaterTestKit.Photo(), ph: "7.25", measuredAt: "2026-10-01T07:30:00"));
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        Assert.Equal(7.25m, body.GetProperty("ph").GetDecimal());
        Assert.Equal(new DateTimeOffset(2026, 10, 1, 7, 30, 0, TimeSpan.FromHours(7)), body.GetProperty("measuredAt").GetDateTimeOffset());
        Assert.NotNull(body.Text("photoUrl"));
        Assert.Equal("Manual", body.Text("inputSource"));
    }

    [Fact]
    public async Task Staff_without_a_grant_in_the_zone_or_without_create_is_refused_and_nothing_is_saved()
    {
        var z = await api.ZoneAsync();
        var other = await api.ZoneAsync();
        var (otherZone, _) = await api.StaffAsync(other.Zone, create: true);
        var (viewOnly, _) = await api.StaffAsync(z.Zone);
        var (revokedClient, revokedAccount) = await api.StaffAsync(z.Zone, create: true);
        var grant = await api.QueryAsync(db => db.AccessGrants.SingleAsync(g => g.AccountId == revokedAccount.Id));
        await api.UpdateAsync<OMTS.Domain.Entities.Identity.AccessGrant>(grant.Id, g => g.StatusId = StatusIds.AccessGrant.Revoked);
        var trader = await api.LoginAsync((await api.AddAccountAsync(NewCode(), Password, SubjectType.Trader)).Username);

        var responses = new[]
        {
            await z.RecordAsync(7m, 20m, client: otherZone),
            await z.RecordAsync(7m, 20m, client: viewOnly),
            // Offline sync after the grant was revoked.
            await z.RecordAsync(7m, 20m, client: revokedClient),
            await z.RecordAsync(7m, 20m, client: trader),
            await z.RecognizeAsync(client: viewOnly),
        };

        foreach (var response in responses)
        {
            Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
            Assert.Equal("MSG02", await response.ErrorCodeAsync());
        }

        Assert.Equal(0, await api.QueryAsync(db => db.WaterMeasurements.CountAsync(m => m.ZoneId == z.Zone.Id)));
    }

    [Fact]
    public async Task Record_of_another_zone_cannot_be_confirmed_or_read_by_id()
    {
        var z = await api.ZoneAsync();
        var draft = await z.RecordedAsync(7m, 20m, confirm: false);
        var (otherZoneManager, _) = await api.StaffAsync((await api.ZoneAsync()).Zone, create: true, edit: true);

        var confirm = await otherZoneManager.PostAsJsonAsync($"/api/water-measurements/{draft.Id()}/confirm", new { });
        var read = await otherZoneManager.GetAsync($"/api/water-measurements/{draft.Id()}");

        Assert.Equal(HttpStatusCode.Forbidden, confirm.StatusCode);
        Assert.Equal("MSG02", await confirm.ErrorCodeAsync());
        Assert.Equal(HttpStatusCode.Forbidden, read.StatusCode);
        Assert.Equal(StatusIds.WaterMeasurement.Draft, await api.QueryAsync(db => db.WaterMeasurements.Where(m => m.Id == draft.Id()).Select(m => m.StatusId).SingleAsync()));
    }

    [Fact]
    public async Task Zone_manager_with_create_records_in_the_granted_zone()
    {
        var z = await api.ZoneAsync();
        var (manager, account) = await api.StaffAsync(z.Zone, create: true);

        var body = await z.RecordedAsync(7.2m, 21m, client: manager);

        Assert.Equal(account.Id, body.GetProperty("measuredBy").GetProperty("id").GetGuid());
        Assert.Equal("Confirmed", body.Text("status"));
        var stored = await api.QueryAsync(db => db.WaterMeasurements.AsNoTracking().SingleAsync(m => m.Id == body.Id()));
        Assert.Equal((6.5m, 8.5m, 15m, 30m), (stored.PhMinUsed!.Value, stored.PhMaxUsed!.Value, stored.SalinityMinUsed!.Value, stored.SalinityMaxUsed!.Value));
        Assert.Equal(MeasurementInputSource.Manual, stored.InputSource);
    }

    // Done when: the photo → draft → confirm flow is usable from Swagger.
    [Fact]
    public async Task Swagger_document_describes_the_water_endpoints()
    {
        var response = await api.CreateClient().GetAsync("/swagger/v1/swagger.json");
        var paths = (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("paths");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        foreach (var path in new[]
        {
            "/api/zones/{zoneId}/water-measurements/recognize", "/api/zones/{zoneId}/water-measurements",
            "/api/zones/{zoneId}/water-measurements/with-photo", "/api/water-measurements/{id}/confirm",
            "/api/water-measurements/{id}/corrections", "/api/zones/{zoneId}/water-thresholds",
        })
        {
            Assert.True(paths.TryGetProperty(path, out _), path);
        }
    }

    private static MultipartFormDataContent ManualForm(Guid operationId, byte[] photo, string ph = "7.1", string measuredAt = "")
    {
        var form = WaterTestKit.PhotoForm(photo);
        form.Add(new StringContent(operationId.ToString()), "clientOperationId");
        form.Add(new StringContent(ph), "ph");
        form.Add(new StringContent("20.5"), "salinity");
        form.Add(new StringContent(measuredAt == "" ? DateTimeOffset.UtcNow.AddMinutes(-5).ToString("O") : measuredAt), "measuredAt");
        form.Add(new StringContent("true"), "confirm");
        return form;
    }

    private static async Task ReplaceStoredPhotoAsync(AuthApiFactory factory, Guid measurementId, byte[] content)
    {
        var key = await factory.QueryAsync(db => db.WaterMeasurements.Where(m => m.Id == measurementId).Select(m => m.PhotoUrl!).SingleAsync());
        await File.WriteAllBytesAsync(Path.Combine(factory.StorageRoot, key), content);
    }
}
