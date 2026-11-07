using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using OMTS.Api.IntegrationTests.Infrastructure;
using OMTS.Application.Common.Interfaces.Messaging;
using OMTS.Application.Common.Models;
using OMTS.Domain.Constants;

namespace OMTS.Api.IntegrationTests.WaterQuality;

/// <summary>
/// WATER-01 re-sends (QA red team): a re-sent client operation answers with the record as it is now and never adds an
/// alert, notification or audit row; a push failure never undoes a measurement; a failed photo store leaves nothing.
/// </summary>
public class WaterIdempotencyRedTeamTests(AuthApiFactory api) : IClassFixture<AuthApiFactory>
{
    [Fact]
    public async Task Re_sent_operation_answers_the_saved_record_as_it_is_now_without_a_second_alert()
    {
        var z = await api.ZoneAsync();
        var (_, manager) = await api.StaffAsync(z.Zone);
        var operationId = Guid.NewGuid();
        var first = await z.RecordAsync(9.1m, 20m, clientOperationId: operationId);
        var id = (await first.Content.ReadFromJsonAsync<JsonElement>()).Id();
        var alerts = await api.AlertsOfAsync(id);
        var audit = (await api.AuditOfAsync(id)).Count;

        // The device did not get the first answer and sends the request again, with another photo.
        var resent = await z.RecordAsync(9.1m, 20m, clientOperationId: operationId);

        Assert.Equal(HttpStatusCode.OK, resent.StatusCode);
        var body = await resent.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(id, body.Id());
        Assert.Equal(("Active", "Active"), (body.Text("status"), body.Text("alertState")));
        // The Owner recorded it: only the zone's manager is alerted (PM decision 03/11).
        Assert.Single(alerts, n => n.RecipientAccountId == manager.Id);
        Assert.DoesNotContain(alerts, n => n.RecipientAccountId == z.OwnerAccount.Id);
        Assert.Equal(alerts.Count, (await api.AlertsOfAsync(id)).Count);
        Assert.Equal(audit, (await api.AuditOfAsync(id)).Count);
        Assert.Equal(1, await api.QueryAsync(db => db.WaterMeasurements.CountAsync(m => m.ZoneId == z.Zone.Id)));
    }

    [Fact]
    public async Task Push_failure_is_only_logged_the_measurement_and_its_in_app_alerts_are_saved()
    {
        await using var own = new WaterApiFactory(services: s => s.AddSingleton<IPushSender, DownPushSender>());
        var z = await own.ZoneAsync();
        var (managerClient, manager) = await own.StaffAsync(z.Zone);

        var response = await z.RecordAsync(9.3m, 20m, client: managerClient);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        Assert.Equal("Active", body.Text("alertState"));
        var stored = await own.QueryAsync(db => db.WaterMeasurements.AsNoTracking().SingleAsync(m => m.Id == body.Id()));
        Assert.Equal(StatusIds.WaterMeasurement.Active, stored.StatusId);
        var alerts = await own.AlertsOfAsync(body.Id());
        Assert.Single(alerts, n => n.RecipientAccountId == z.OwnerAccount.Id);
        Assert.Single(alerts, n => n.RecipientAccountId == manager.Id);
        Assert.Contains(own.Logs.Lines, line => line.Contains("Push WATER_ALERT") && line.Contains("failed"));
    }

    [Fact]
    public async Task Failed_photo_storage_answers_502_and_leaves_no_measurement_audit_row_or_notification()
    {
        await using var broken = new WaterApiFactory(services: s => s.AddSingleton<OMTS.Application.Common.Interfaces.Storage.IFileStorage, FailingFileStorage>());
        var z = await broken.ZoneAsync();

        var recognized = await z.RecognizeAsync("ph9.4_sal20");

        Assert.Equal(HttpStatusCode.BadGateway, recognized.StatusCode);
        Assert.Equal("MSG-GEN-03", await recognized.ErrorCodeAsync());
        Assert.Equal(0, await broken.QueryAsync(db => db.WaterMeasurements.CountAsync()));
        Assert.Equal(0, await broken.QueryAsync(db => db.AuditEvents.CountAsync(e => e.RecordType == "WaterMeasurement")));
        Assert.Equal(0, await broken.QueryAsync(db => db.Notifications.CountAsync(n => n.Type == "WATER_ALERT")));
    }

    /// <summary>A push provider that is down: every send throws.</summary>
    private sealed class DownPushSender : IPushSender
    {
        public Task<DeliveryResult> SendAsync(PushMessage message, CancellationToken cancellationToken = default) =>
            throw new HttpRequestException("push provider down");
    }
}
