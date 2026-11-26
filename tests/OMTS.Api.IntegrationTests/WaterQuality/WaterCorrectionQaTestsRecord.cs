using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using OMTS.Api.IntegrationTests.Infrastructure;
using OMTS.Domain.Constants;
using OMTS.Domain.Entities.Farming;

namespace OMTS.Api.IntegrationTests.WaterQuality;

/// <summary>QA of what a void does to the record: thresholds, audit, reason, and what was removed.</summary>
public class WaterCorrectionQaTestsRecord(AuthApiFactory api) : IClassFixture<AuthApiFactory>
{
    private static readonly string[] RemovedFields = ["currentEditors", "editingNotice", "rowVersion", "recordVersion"];

    private Task<OMTS.Domain.Entities.Cultivation.WaterMeasurement> StoredAsync(Guid id) =>
        api.QueryAsync(db => db.WaterMeasurements.AsNoTracking().SingleAsync(m => m.Id == id));

    // BR-WATER-04: the copy of the moment it was saved, whatever the zone holds now, for the result and for the range in the message.
    [Fact]
    public async Task The_void_keeps_all_six_copied_thresholds()
    {
        var z = await api.ZoneAsync();
        var (_, manager) = await api.StaffAsync(z.Zone);
        var saved = await z.RecordedAsync(8.7m, 20m, measuredAt: DateTimeOffset.UtcNow.AddHours(-1));
        await api.UpdateAsync<FarmingZone>(z.Zone.Id, zone => (zone.PhMin, zone.PhMax, zone.SalinityMin, zone.SalinityMax, zone.TemperatureMin, zone.TemperatureMax) = (1m, 2m, 3m, 4m, 5m, 6m));

        var voided = await z.Owner.VoidAsync(saved.Id());

        Assert.Equal(HttpStatusCode.OK, voided.StatusCode);
        var alert = Assert.Single(await api.AlertsOfAsync(saved.Id()), n => n.RecipientAccountId == manager.Id);
        Assert.Contains("6.5", alert.Body);
        Assert.Contains("8.5", alert.Body);
        var stored = await StoredAsync(saved.Id());
        Assert.Equal((6.5m, 8.5m, 15m, 30m, 10m, 35m), (stored.PhMinUsed, stored.PhMaxUsed, stored.SalinityMinUsed, stored.SalinityMaxUsed, stored.TemperatureMinUsed, stored.TemperatureMaxUsed));
    }

    [Fact]
    public async Task A_measurement_saved_without_thresholds_stays_without_after_the_zone_got_some()
    {
        var z = await api.ZoneAsync(thresholds: false);
        var (_, manager) = await api.StaffAsync(z.Zone);
        var saved = await z.RecordedAsync(7m, 20m);
        await api.UpdateAsync<FarmingZone>(z.Zone.Id, zone => (zone.PhMin, zone.PhMax) = (6.5m, 8.5m));

        var voided = await z.Owner.VoidAsync(saved.Id());

        Assert.Equal(HttpStatusCode.OK, voided.StatusCode);
        var stored = await StoredAsync(saved.Id());
        Assert.Null(stored.PhMaxUsed);
        Assert.DoesNotContain(await api.AlertsOfAsync(saved.Id()), n => n.RecipientAccountId == manager.Id);
    }

    // POST-1: zone, meter photo, thresholds and recorder cannot be changed.
    [Fact]
    public async Task Zone_photo_thresholds_and_recorder_in_the_body_are_ignored()
    {
        var z = await api.ZoneAsync();
        var other = await api.ZoneAsync();
        var saved = await z.RecordedAsync(7m, 20m);
        var before = await StoredAsync(saved.Id());

        var response = await z.Owner.PostAsJsonAsync($"/api/water-measurements/{saved.Id()}/void", new
        {
            zoneId = other.Zone.Id,
            photoKey = "water-meter-photos/x/2026/11/y.png",
            photoUrl = "x",
            phMaxUsed = 14m,
            thresholdsUsed = new { phMax = 14m },
            measuredByAccountId = Guid.NewGuid(),
            raftId = other.Raft.Id,
            ph = 7.1m,
            reason = "Sai số",
        });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var after = await StoredAsync(saved.Id());
        Assert.Equal((before.ZoneId, before.RaftId, before.PhotoUrl, before.PhMaxUsed, before.MeasuredByAccountId, before.ClientOperationId, before.Ph),
            (after.ZoneId, after.RaftId, after.PhotoUrl, after.PhMaxUsed, after.MeasuredByAccountId, after.ClientOperationId, after.Ph));
        Assert.Equal(StatusIds.WaterMeasurement.Voided, after.StatusId);
        Assert.Equal(before.CreatedAt, after.CreatedAt);
    }

    [Fact]
    public async Task A_refused_change_leaves_no_audit_notification_or_change()
    {
        var z = await api.ZoneAsync();
        var (_, manager) = await api.StaffAsync(z.Zone);
        var saved = await z.RecordedAsync(9m, 20m);
        var alertsBefore = (await api.AlertsOfAsync(saved.Id())).Count;
        var storedBefore = await StoredAsync(saved.Id());

        var responses = new[]
        {
            await z.Owner.VoidAsync(saved.Id(), reason: null),
            await z.Owner.VoidAsync(saved.Id(), reason: new string('x', 1001)),
        };

        Assert.All(responses, r => Assert.Equal(HttpStatusCode.BadRequest, r.StatusCode));
        var stored = await StoredAsync(saved.Id());
        Assert.Equal((storedBefore.Ph, storedBefore.AlertedAt, storedBefore.StatusId, storedBefore.RowVersion), (stored.Ph, stored.AlertedAt, stored.StatusId, stored.RowVersion));
        Assert.Equal(alertsBefore, (await api.AlertsOfAsync(saved.Id())).Count);
        Assert.Empty(await api.WithdrawalsOfAsync(saved.Id()));
        Assert.DoesNotContain(await api.AuditOfAsync(saved.Id()), e => e.Action != "WaterMeasurement.Created");
    }

    [Fact]
    public async Task A_second_void_changes_nothing_and_sends_nothing()
    {
        var z = await api.ZoneAsync();
        var (_, manager) = await api.StaffAsync(z.Zone);
        var saved = await z.RecordedAsync(9m, 20m);
        Assert.Equal(HttpStatusCode.OK, (await z.Owner.VoidAsync(saved.Id(), reason: "Lần đầu")).StatusCode);

        var again = await z.Owner.VoidAsync(saved.Id(), reason: "Lần hai");

        Assert.Equal(HttpStatusCode.Conflict, again.StatusCode);
        Assert.Equal("MSG-GEN-09", await again.ErrorCodeAsync());

        var stored = await StoredAsync(saved.Id());
        Assert.Equal(("Lần đầu", 9m, StatusIds.WaterMeasurement.Voided), (stored.VoidReason, stored.Ph, stored.StatusId));
        Assert.Single(await api.WithdrawalsOfAsync(saved.Id()), n => n.RecipientAccountId == manager.Id);
        Assert.Single(await api.AuditOfAsync(saved.Id()), e => e.Action == "WaterMeasurement.Voided");
    }

    [Fact]
    public async Task The_longest_reason_is_saved_and_one_more_is_refused_on_void()
    {
        var z = await api.ZoneAsync();
        var saved = await z.RecordedAsync(7m, 20m);
        var longest = new string('ố', 1000);

        Assert.Equal(HttpStatusCode.BadRequest, (await z.Owner.VoidAsync(saved.Id(), reason: longest + "x")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await z.Owner.VoidAsync(saved.Id(), reason: longest)).StatusCode);
        Assert.Equal(longest, (await StoredAsync(saved.Id())).VoidReason);
    }

    // BR-WATER-06: "voided and recorded again, and its meter photo can be chosen again from the phone".
    [Fact]
    public async Task After_a_void_the_same_picture_can_be_recorded_again_but_not_the_used_key()
    {
        var z = await api.ZoneAsync();
        var key = await z.Owner.PhotoKeyAsync(z.Zone.Id);
        var saved = await (await z.RecordAsync(7m, 20m, photoKey: key)).Content.ReadFromJsonAsync<JsonElement>();
        await z.Owner.VoidAsync(saved.Id());

        var sameKey = await z.RecordAsync(7m, 20m, photoKey: key);
        var chosenAgain = await z.RecordAsync(7m, 20m);

        Assert.Equal(HttpStatusCode.BadRequest, sameKey.StatusCode);
        Assert.Equal(HttpStatusCode.Created, chosenAgain.StatusCode);
    }

    [Fact]
    public async Task No_route_of_the_editing_notice_remains_and_the_measurement_route_has_only_read_and_void()
    {
        var z = await api.ZoneAsync();
        var saved = await z.RecordedAsync(7m, 20m);
        var patterns = api.Services.GetRequiredService<EndpointDataSource>().Endpoints.OfType<RouteEndpoint>()
            .Select(e => e.RoutePattern.RawText ?? string.Empty).ToList();

        Assert.DoesNotContain(patterns, p => p.Contains("editing", StringComparison.OrdinalIgnoreCase));
        Assert.Equal(
            ["api/water-measurements/zones", "api/water-measurements/{id:guid}", "api/water-measurements/{id:guid}/void"],
            patterns.Where(p => p.StartsWith("api/water-measurements", StringComparison.Ordinal)).Order(StringComparer.Ordinal).ToList());
        var one = $"/api/water-measurements/{saved.Id()}";
        Assert.Equal(HttpStatusCode.MethodNotAllowed, (await z.Owner.PutAsJsonAsync(one, new { })).StatusCode);
        Assert.Equal(HttpStatusCode.MethodNotAllowed, (await z.Owner.DeleteAsync(one)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await z.Owner.PostAsJsonAsync($"{one}/corrections", new { })).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await z.Owner.PostAsync($"{one}/editing", null)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await z.Owner.DeleteAsync($"{one}/editing")).StatusCode);
    }

    [Fact]
    public async Task No_answer_of_record_list_detail_or_void_carries_a_version_or_an_editor_and_a_stale_header_is_ignored()
    {
        var z = await api.ZoneAsync();
        var saved = await z.RecordedAsync(7m, 20m);
        var id = saved.Id();
        var staleVoid = new HttpRequestMessage(HttpMethod.Post, $"/api/water-measurements/{id}/void")
        {
            Content = JsonContent.Create(new { reason = "Sai số" }),
        };
        staleVoid.Headers.TryAddWithoutValidation("If-Match", "\"stale\"");

        var voided = await z.Owner.SendAsync(staleVoid);
        Assert.Equal(HttpStatusCode.OK, voided.StatusCode);
        var list = await z.Owner.GetFromJsonAsync<JsonElement>(z.Base);
        var answers = new List<JsonElement>
        {
            saved,
            await voided.Content.ReadFromJsonAsync<JsonElement>(),
            await z.Owner.GetFromJsonAsync<JsonElement>($"/api/water-measurements/{id}"),
            list.GetProperty("items")[0],
        };

        foreach (var answer in answers)
        {
            Assert.All(RemovedFields, field => Assert.False(answer.TryGetProperty(field, out _), field));
        }
    }
}
