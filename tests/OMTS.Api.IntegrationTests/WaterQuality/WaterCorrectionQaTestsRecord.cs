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

/// <summary>QA of what a correction or a void does to the record: thresholds, audit, reason, time, raft, and what was removed.</summary>
public class WaterCorrectionQaTestsRecord(AuthApiFactory api) : IClassFixture<AuthApiFactory>
{
    private static readonly string[] RemovedFields = ["currentEditors", "editingNotice", "rowVersion", "recordVersion"];

    private Task<OMTS.Domain.Entities.Cultivation.WaterMeasurement> StoredAsync(Guid id) =>
        api.QueryAsync(db => db.WaterMeasurements.AsNoTracking().SingleAsync(m => m.Id == id));

    // BR-WATER-04: the copy of the moment it was saved, whatever the zone holds now, for the result and for the range in the message.
    [Fact]
    public async Task Every_correction_and_the_void_keep_all_six_copied_thresholds_and_use_them()
    {
        var z = await api.ZoneAsync();
        var (_, manager) = await api.StaffAsync(z.Zone);
        var saved = await z.RecordedAsync(8m, 20m, measuredAt: DateTimeOffset.UtcNow.AddHours(-1));
        await api.UpdateAsync<FarmingZone>(z.Zone.Id, zone => (zone.PhMin, zone.PhMax, zone.SalinityMin, zone.SalinityMax, zone.TemperatureMin, zone.TemperatureMax) = (1m, 2m, 3m, 4m, 5m, 6m));

        var inside = await (await z.Owner.CorrectAsync(saved.Id(), 8.2m, 21m)).Content.ReadFromJsonAsync<JsonElement>();
        var outside = await z.Owner.CorrectAsync(saved.Id(), 8.7m, 21m);
        await z.Owner.VoidAsync(saved.Id());

        Assert.Equal("None", inside.Text("alertState"));
        Assert.Equal(HttpStatusCode.OK, outside.StatusCode);
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

        var body = await (await z.Owner.CorrectAsync(saved.Id(), 14m, 50m)).Content.ReadFromJsonAsync<JsonElement>();

        Assert.Equal("None", body.Text("alertState"));
        Assert.Equal(JsonValueKind.Null, body.GetProperty("thresholdsUsed").GetProperty("phMax").ValueKind);
        Assert.DoesNotContain(await api.AlertsOfAsync(saved.Id()), n => n.RecipientAccountId == manager.Id);
    }

    // POST-1: zone, meter photo and thresholds cannot be changed.
    [Fact]
    public async Task Zone_photo_thresholds_and_recorder_in_the_body_are_ignored()
    {
        var z = await api.ZoneAsync();
        var other = await api.ZoneAsync();
        var saved = await z.RecordedAsync(7m, 20m);
        var before = await StoredAsync(saved.Id());

        var response = await z.Owner.PostAsJsonAsync($"/api/water-measurements/{saved.Id()}/corrections", new
        {
            zoneId = other.Zone.Id,
            photoKey = "water-meter-photos/x/2026/11/y.png",
            photoUrl = "x",
            phMaxUsed = 14m,
            thresholdsUsed = new { phMax = 14m },
            measuredByAccountId = Guid.NewGuid(),
            statusId = StatusIds.WaterMeasurement.Voided,
            raftId = z.Raft.Id,
            ph = 7.1m,
            salinity = 20m,
            temperature = 25m,
            measuredAt = DateTimeOffset.UtcNow.AddMinutes(-5),
            reason = "Sai số",
        });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var after = await StoredAsync(saved.Id());
        Assert.Equal((before.ZoneId, before.PhotoUrl, before.PhMaxUsed, before.MeasuredByAccountId, before.ClientOperationId, before.StatusId),
            (after.ZoneId, after.PhotoUrl, after.PhMaxUsed, after.MeasuredByAccountId, after.ClientOperationId, after.StatusId));
        Assert.Equal(before.CreatedAt, after.CreatedAt);
    }

    [Fact]
    public async Task A_correction_audits_every_value_before_and_after_with_the_trimmed_reason_and_the_zone()
    {
        var z = await api.ZoneAsync();
        var raft = await api.RaftAsync(z.Zone);
        var firstTime = DateTimeOffset.UtcNow.AddHours(-3);
        var secondTime = DateTimeOffset.UtcNow.AddHours(-2);
        var saved = await z.RecordedAsync(9.1m, 20m, measuredAt: firstTime);

        await z.Owner.CorrectAsync(saved.Id(), 7.2m, 21.5m, reason: "  Sai số  ", measuredAt: secondTime, temperature: 26m, raftId: raft.Id);

        var audit = Assert.Single(await api.AuditOfAsync(saved.Id()), e => e.Action == "WaterMeasurement.Corrected");
        Assert.Equal(("Sai số", z.Zone.Id), (audit.Reason, audit.ZoneId));
        using var before = JsonDocument.Parse(audit.Before!);
        using var after = JsonDocument.Parse(audit.After!);
        Assert.Equal(("ACTIVE", z.Raft.Id, 9.1m, 20m, 25m, true), Values(before.RootElement));
        Assert.Equal(("ACTIVE", raft.Id, 7.2m, 21.5m, 26m, false), Values(after.RootElement));
        Assert.Equal(firstTime.ToUnixTimeMilliseconds(), before.RootElement.GetProperty("MeasuredAt").GetDateTimeOffset().ToUnixTimeMilliseconds());
        Assert.Equal(secondTime.ToUnixTimeMilliseconds(), after.RootElement.GetProperty("MeasuredAt").GetDateTimeOffset().ToUnixTimeMilliseconds());
    }

    private static (string?, Guid, decimal, decimal, decimal, bool) Values(JsonElement e) => (
        e.GetProperty("Status").GetString(), e.GetProperty("RaftId").GetGuid(), e.GetProperty("Ph").GetDecimal(),
        e.GetProperty("Salinity").GetDecimal(), e.GetProperty("Temperature").GetDecimal(), e.GetProperty("ThresholdExceeded").GetBoolean());

    [Fact]
    public async Task A_refused_change_leaves_no_audit_notification_or_change()
    {
        var z = await api.ZoneAsync();
        var (_, manager) = await api.StaffAsync(z.Zone);
        var saved = await z.RecordedAsync(9m, 20m);
        var alertsBefore = (await api.AlertsOfAsync(saved.Id())).Count;
        var storedBefore = await StoredAsync(saved.Id());
        var otherZone = await api.ZoneAsync();

        var responses = new[]
        {
            await z.Owner.CorrectAsync(saved.Id(), 7m, 20m, reason: "  "),
            await z.Owner.CorrectAsync(saved.Id(), 7m, 20m, raftId: otherZone.Raft.Id),
            await z.Owner.CorrectAsync(saved.Id(), 7.001m, 20m),
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
    public async Task A_second_void_and_a_correction_after_a_void_change_nothing_and_send_nothing()
    {
        var z = await api.ZoneAsync();
        var (_, manager) = await api.StaffAsync(z.Zone);
        var saved = await z.RecordedAsync(9m, 20m);
        Assert.Equal(HttpStatusCode.OK, (await z.Owner.VoidAsync(saved.Id(), reason: "Lần đầu")).StatusCode);

        var again = await z.Owner.VoidAsync(saved.Id(), reason: "Lần hai");
        var corrected = await z.Owner.CorrectAsync(saved.Id(), 7m, 20m);

        foreach (var response in new[] { again, corrected })
        {
            Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
            Assert.Equal("MSG-GEN-09", await response.ErrorCodeAsync());
        }

        var stored = await StoredAsync(saved.Id());
        Assert.Equal(("Lần đầu", 9m, StatusIds.WaterMeasurement.Voided), (stored.VoidReason, stored.Ph, stored.StatusId));
        Assert.Single(await api.WithdrawalsOfAsync(saved.Id()), n => n.RecipientAccountId == manager.Id);
        Assert.Single(await api.AuditOfAsync(saved.Id()), e => e.Action == "WaterMeasurement.Voided");
        Assert.DoesNotContain(await api.AuditOfAsync(saved.Id()), e => e.Action == "WaterMeasurement.Corrected");
    }

    [Fact]
    public async Task The_longest_reason_is_saved_and_one_more_is_refused_on_correct_and_void()
    {
        var z = await api.ZoneAsync();
        var saved = await z.RecordedAsync(7m, 20m);
        var longest = new string('ố', 1000);

        Assert.Equal(HttpStatusCode.OK, (await z.Owner.CorrectAsync(saved.Id(), 7.1m, 20m, reason: longest)).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await z.Owner.CorrectAsync(saved.Id(), 7.1m, 20m, reason: longest + "x")).StatusCode);
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

    // F-03: same finding as F-04 of the W1 report, on the correction path: BR-WATER-03 and step 4 "not later than now".
    [Fact]
    public async Task F03_a_corrected_time_later_than_now_is_refused()
    {
        var z = await api.ZoneAsync();
        var saved = await z.RecordedAsync(7m, 20m);

        var response = await z.Owner.CorrectAsync(saved.Id(), 7m, 20m, measuredAt: DateTimeOffset.UtcNow.AddMinutes(4));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task A_time_too_far_ahead_too_old_or_missing_is_refused_on_the_time_field()
    {
        var z = await api.ZoneAsync();
        var saved = await z.RecordedAsync(7m, 20m);
        var tooOld = DateTimeOffset.Parse("1999-12-31T08:00:00+07:00", System.Globalization.CultureInfo.InvariantCulture);

        foreach (var time in new DateTimeOffset?[] { DateTimeOffset.UtcNow.AddMinutes(10), tooOld, null })
        {
            var response = await z.Owner.PostAsJsonAsync($"/api/water-measurements/{saved.Id()}/corrections", new
            {
                raftId = z.Raft.Id, ph = 7.1m, salinity = 20m, temperature = 25m, measuredAt = time, reason = "Sai số",
            });
            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
            Assert.Equal("Measured at", (await response.Content.ReadFromJsonAsync<JsonElement>()).Text("field"));
        }
    }

    [Fact]
    public async Task A_corrected_time_without_an_offset_is_vietnam_time()
    {
        var z = await api.ZoneAsync();
        var saved = await z.RecordedAsync(7m, 20m);
        var vietnam = DateTimeOffset.UtcNow.AddDays(-3).ToOffset(TimeSpan.FromHours(7));
        var text = vietnam.ToString("yyyy-MM-dd'T'HH:mm:ss", System.Globalization.CultureInfo.InvariantCulture);

        var response = await z.Owner.PostAsJsonAsync($"/api/water-measurements/{saved.Id()}/corrections", new
        {
            raftId = z.Raft.Id, ph = 7.1m, salinity = 20m, temperature = 25m, measuredAt = text, reason = "Sai số",
        });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var stored = await StoredAsync(saved.Id());
        Assert.Equal(vietnam.ToUnixTimeSeconds(), stored.MeasuredAt.ToUnixTimeSeconds());
    }

    [Fact]
    public async Task A_correction_at_a_raft_discontinued_since_still_alerts_and_names_that_raft()
    {
        var z = await api.ZoneAsync();
        var (_, manager) = await api.StaffAsync(z.Zone);
        var saved = await z.RecordedAsync(7m, 20m);
        await api.UpdateAsync<Raft>(z.Raft.Id, r => r.StatusId = StatusIds.Usage.Discontinued);

        Assert.Equal(HttpStatusCode.OK, (await z.Owner.CorrectAsync(saved.Id(), 9m, 20m)).StatusCode);

        var alert = Assert.Single(await api.AlertsOfAsync(saved.Id()), n => n.RecipientAccountId == manager.Id);
        Assert.Contains($"raft {z.Raft.Code},", alert.Body);
    }

    [Fact]
    public async Task No_route_of_the_editing_notice_remains_and_the_measurement_route_has_only_read_correct_and_void()
    {
        var z = await api.ZoneAsync();
        var saved = await z.RecordedAsync(7m, 20m);
        var patterns = api.Services.GetRequiredService<EndpointDataSource>().Endpoints.OfType<RouteEndpoint>()
            .Select(e => e.RoutePattern.RawText ?? string.Empty).ToList();

        Assert.DoesNotContain(patterns, p => p.Contains("editing", StringComparison.OrdinalIgnoreCase));
        Assert.Equal(
            ["api/water-measurements/{id:guid}", "api/water-measurements/{id:guid}/corrections", "api/water-measurements/{id:guid}/void"],
            patterns.Where(p => p.StartsWith("api/water-measurements", StringComparison.Ordinal)).Order(StringComparer.Ordinal).ToList());
        var one = $"/api/water-measurements/{saved.Id()}";
        Assert.Equal(HttpStatusCode.MethodNotAllowed, (await z.Owner.PutAsJsonAsync(one, new { })).StatusCode);
        Assert.Equal(HttpStatusCode.MethodNotAllowed, (await z.Owner.DeleteAsync(one)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await z.Owner.PostAsync($"{one}/editing", null)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await z.Owner.DeleteAsync($"{one}/editing")).StatusCode);
    }

    [Fact]
    public async Task No_answer_of_record_list_detail_correct_or_void_carries_a_version_or_an_editor_and_a_stale_header_is_ignored()
    {
        var z = await api.ZoneAsync();
        var saved = await z.RecordedAsync(7m, 20m);
        var id = saved.Id();
        var correct = new HttpRequestMessage(HttpMethod.Post, $"/api/water-measurements/{id}/corrections")
        {
            Content = JsonContent.Create(new { raftId = z.Raft.Id, ph = 7.1m, salinity = 20m, temperature = 25m, measuredAt = DateTimeOffset.UtcNow.AddMinutes(-5), reason = "Sai số" }),
        };
        correct.Headers.TryAddWithoutValidation("If-Match", "\"stale\"");

        var corrected = await z.Owner.SendAsync(correct);
        Assert.Equal(HttpStatusCode.OK, corrected.StatusCode);
        var list = await z.Owner.GetFromJsonAsync<JsonElement>(z.Base);
        var answers = new List<JsonElement>
        {
            saved,
            await corrected.Content.ReadFromJsonAsync<JsonElement>(),
            await z.Owner.GetFromJsonAsync<JsonElement>($"/api/water-measurements/{id}"),
            list.GetProperty("items")[0],
            await (await z.Owner.VoidAsync(id)).Content.ReadFromJsonAsync<JsonElement>(),
        };

        foreach (var answer in answers)
        {
            Assert.All(RemovedFields, field => Assert.False(answer.TryGetProperty(field, out _), field));
        }
    }
}
