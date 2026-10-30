using System.Net.Http.Json;
using System.Net;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using OMTS.Api.IntegrationTests.Infrastructure;
using OMTS.Application.Common.Interfaces.Messaging;
using OMTS.Application.Common.Models.Messaging;
using OMTS.Application.Common.Models;
using OMTS.Domain.Constants;

namespace OMTS.Api.IntegrationTests.WaterQuality;

/// <summary>
/// ENV-01 offline re-sends (NFR-15, QA red team): a re-sent client operation answers with the record as it is
/// now, never calls OCR or stores the photo again, and never adds an alert, notification or audit row; a
/// push failure never undoes a confirmation.
/// </summary>
public class WaterIdempotencyRedTeamTests(AuthApiFactory api) : IClassFixture<AuthApiFactory>
{
    [Fact]
    public async Task Re_sent_draft_operation_after_its_confirmation_answers_the_confirmed_record_without_a_second_alert()
    {
        var z = await api.ZoneAsync();
        var operationId = Guid.NewGuid();
        var draft = await z.RecordAsync(9.1m, 20m, confirm: false, clientOperationId: operationId);
        var draftId = (await draft.Content.ReadFromJsonAsync<JsonElement>()).Id();
        Assert.Empty(await api.AlertsOfAsync(draftId));
        var confirm = await z.Owner.PostAsJsonAsync($"/api/water-measurements/{draftId}/confirm", new { });
        Assert.Equal(HttpStatusCode.OK, confirm.StatusCode);
        var alertsAfterConfirm = await api.AlertsOfAsync(draftId);
        var auditAfterConfirm = (await api.AuditOfAsync(draftId)).Count;

        // The device did not get the first answer and re-sends both versions of its request.
        var resentDraft = await z.RecordAsync(9.1m, 20m, confirm: false, clientOperationId: operationId);
        var resentConfirmed = await z.RecordAsync(9.1m, 20m, confirm: true, clientOperationId: operationId);

        foreach (var response in new[] { resentDraft, resentConfirmed })
        {
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            var body = await response.Content.ReadFromJsonAsync<JsonElement>();
            Assert.Equal(draftId, body.Id());
            Assert.Equal("Confirmed", body.Text("status"));
            Assert.Equal("Active", body.Text("alertState"));
        }

        Assert.Single(alertsAfterConfirm, n => n.RecipientAccountId == z.OwnerAccount.Id);
        Assert.Equal(alertsAfterConfirm.Count, (await api.AlertsOfAsync(draftId)).Count);
        Assert.Equal(auditAfterConfirm, (await api.AuditOfAsync(draftId)).Count);
        Assert.Equal(1, await api.QueryAsync(db => db.WaterMeasurements.CountAsync(m => m.ZoneId == z.Zone.Id)));
    }

    [Fact]
    public async Task Re_sent_recognition_answers_the_saved_draft_without_calling_ocr_or_storing_the_photo_again()
    {
        await using var own = new WaterApiFactory();
        var z = await own.ZoneAsync();
        var operationId = Guid.NewGuid();

        var first = await z.RecognizeAsync("ph7.9_sal21", operationId);
        // Same operation, a photo the fake would fail on: OCR must not run again.
        var again = await z.RecognizeAsync("ocr-fail", operationId);
        var firstBody = await first.Content.ReadFromJsonAsync<JsonElement>();
        var againBody = await again.Content.ReadFromJsonAsync<JsonElement>();

        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        Assert.Equal(HttpStatusCode.OK, again.StatusCode);
        Assert.Equal(firstBody.GetProperty("measurement").Id(), againBody.GetProperty("measurement").Id());
        Assert.Equal("NotCalled", againBody.GetProperty("ocr").Text("status"));
        Assert.Equal(7.9m, againBody.GetProperty("measurement").GetProperty("ph").GetDecimal());
        Assert.Equal(1, await own.QueryAsync(db => db.WaterMeasurements.CountAsync(m => m.ZoneId == z.Zone.Id)));
        Assert.Equal(1, StoredPhotoCount(own));
    }

    [Fact]
    public async Task Re_sent_manual_entry_with_a_photo_stores_the_photo_once()
    {
        await using var own = new WaterApiFactory();
        var z = await own.ZoneAsync();
        var operationId = Guid.NewGuid();

        var first = await z.Owner.PostAsync($"{z.Base}/with-photo", ManualForm(operationId));
        var again = await z.Owner.PostAsync($"{z.Base}/with-photo", ManualForm(operationId));

        Assert.Equal(HttpStatusCode.Created, first.StatusCode);
        Assert.Equal(HttpStatusCode.OK, again.StatusCode);
        Assert.Equal((await first.Content.ReadFromJsonAsync<JsonElement>()).Id(), (await again.Content.ReadFromJsonAsync<JsonElement>()).Id());
        Assert.Equal(1, await own.QueryAsync(db => db.WaterMeasurements.CountAsync(m => m.ZoneId == z.Zone.Id)));
        Assert.Equal(1, StoredPhotoCount(own));
    }

    [Fact]
    public async Task Push_failure_is_only_logged_the_confirmation_and_its_in_app_alerts_are_saved()
    {
        await using var own = new WaterApiFactory(services: s => s.AddSingleton<IPushSender, DownPushSender>());
        var z = await own.ZoneAsync();

        var response = await z.RecordAsync(9.3m, 20m);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        Assert.Equal("Active", body.Text("alertState"));
        var stored = await own.QueryAsync(db => db.WaterMeasurements.AsNoTracking().SingleAsync(m => m.Id == body.Id()));
        Assert.Equal(StatusIds.WaterMeasurement.Confirmed, stored.StatusId);
        Assert.Single(await own.AlertsOfAsync(body.Id()), n => n.RecipientAccountId == z.OwnerAccount.Id);
        Assert.Contains(own.Logs.Lines, line => line.Contains("Push WATER_ALERT") && line.Contains("failed"));
    }

    [Fact]
    public async Task Failed_photo_storage_leaves_no_measurement_audit_row_or_notification()
    {
        await using var broken = new WaterApiFactory(services: s => s.AddSingleton<OMTS.Application.Common.Interfaces.Storage.IFileStorage, FailingFileStorage>());
        var z = await broken.ZoneAsync();
        var form = WaterTestKit.PhotoForm(WaterTestKit.Photo());
        form.Add(new StringContent(Guid.NewGuid().ToString()), "clientOperationId");
        form.Add(new StringContent("9.4"), "ph");
        form.Add(new StringContent("20"), "salinity");
        form.Add(new StringContent(DateTimeOffset.UtcNow.AddMinutes(-5).ToString("O")), "measuredAt");
        form.Add(new StringContent("true"), "confirm");

        var recognized = await z.RecognizeAsync("ph9.4_sal20");
        var manual = await z.Owner.PostAsync($"{z.Base}/with-photo", form);

        Assert.Equal(HttpStatusCode.BadGateway, recognized.StatusCode);
        Assert.Equal(HttpStatusCode.BadGateway, manual.StatusCode);
        Assert.Equal("MSG05", await manual.ErrorCodeAsync());
        Assert.Equal(0, await broken.QueryAsync(db => db.WaterMeasurements.CountAsync()));
        Assert.Equal(0, await broken.QueryAsync(db => db.AuditEvents.CountAsync(e => e.RecordType == "WaterMeasurement")));
        Assert.Equal(0, await broken.QueryAsync(db => db.Notifications.CountAsync(n => n.Type == "WATER_ALERT")));
    }

    private static int StoredPhotoCount(AuthApiFactory factory) =>
        Directory.Exists(factory.StorageRoot)
            ? Directory.EnumerateFiles(factory.StorageRoot, "*", SearchOption.AllDirectories).Count()
            : 0;

    private static MultipartFormDataContent ManualForm(Guid operationId)
    {
        var form = WaterTestKit.PhotoForm(WaterTestKit.Photo("ph7.1_sal20"));
        form.Add(new StringContent(operationId.ToString()), "clientOperationId");
        form.Add(new StringContent("7.1"), "ph");
        form.Add(new StringContent("20.5"), "salinity");
        form.Add(new StringContent(DateTimeOffset.UtcNow.AddMinutes(-5).ToString("O")), "measuredAt");
        form.Add(new StringContent("true"), "confirm");
        return form;
    }

    /// <summary>A push provider that is down: every send throws.</summary>
    private sealed class DownPushSender : IPushSender
    {
        public Task<DeliveryResult> SendAsync(PushMessage message, CancellationToken cancellationToken = default) =>
            throw new HttpRequestException("push provider down");
    }
}
