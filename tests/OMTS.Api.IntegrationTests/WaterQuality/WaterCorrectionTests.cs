using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using OMTS.Api.IntegrationTests.Infrastructure;
using OMTS.Domain.Entities.Farming;

namespace OMTS.Api.IntegrationTests.WaterQuality;

/// <summary>
/// WATER-02 on one record: a correction changes the same record against its own thresholds, a void is final, both need a
/// reason and are audited with the values before and after. Who may do it: <see cref="WaterCorrectionAccessTests"/>; who is
/// told: <see cref="WaterCorrectionNotifyMatrixTests"/>; parallel saves: <see cref="WaterCorrectionConflictTests"/>.
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
        Assert.Equal(JsonValueKind.Null, corrected.GetProperty("alertedAt").ValueKind);
        Assert.Equal(alertsBefore, (await api.AlertsOfAsync(original.Id())).Count);
        Assert.Equal(1, await api.QueryAsync(db => db.WaterMeasurements.CountAsync(m => m.ZoneId == z.Zone.Id)));
    }

    [Fact]
    public async Task Correction_keeps_zone_photo_recorder_and_thresholds_and_is_audited_once_with_before_after_and_reason()
    {
        var z = await api.ZoneAsync();
        var key = await z.Owner.PhotoKeyAsync(z.Zone.Id);
        var id = (await (await z.RecordAsync(7.4m, 21m, photoKey: key)).Content.ReadFromJsonAsync<JsonElement>()).Id();

        await z.Owner.CorrectAsync(id, 7.5m, 21.5m, reason: "Sai số");

        var stored = await api.QueryAsync(db => db.WaterMeasurements.AsNoTracking().SingleAsync(m => m.Id == id));
        Assert.Equal((key, z.Raft.Id, z.Zone.Id, 7.5m, z.OwnerAccount.Id), (stored.PhotoUrl, stored.RaftId, stored.ZoneId, stored.Ph, stored.MeasuredByAccountId!.Value));
        Assert.Equal((6.5m, 8.5m), (stored.PhMinUsed!.Value, stored.PhMaxUsed!.Value));
        var audit = Assert.Single(await api.AuditOfAsync(id), e => e.Action == "WaterMeasurement.Corrected");
        Assert.Equal("Sai số", audit.Reason);
        Assert.Contains("\"Ph\":7.4", audit.Before);
        Assert.Contains("\"Ph\":7.5", audit.After);
    }

    [Fact]
    public async Task A_new_result_is_worked_out_against_the_thresholds_of_the_measurement_not_todays()
    {
        var z = await api.ZoneAsync();
        var original = await z.RecordedAsync(8.0m, 20m);
        // Today's thresholds are wider; the correction must still use pH max 8.5 stored with the measurement.
        await api.UpdateAsync<FarmingZone>(z.Zone.Id, zone => zone.PhMax = 9.5m);

        var corrected = await (await z.Owner.CorrectAsync(original.Id(), 9.0m, 20m)).Content.ReadFromJsonAsync<JsonElement>();

        Assert.Equal("Active", corrected.Text("alertState"));
        Assert.Equal("pH 9.00 > 8.50", corrected.Text("violatedValues"));
        Assert.Equal(8.5m, corrected.GetProperty("thresholdsUsed").GetProperty("phMax").GetDecimal());
    }

    [Fact]
    public async Task A_voided_record_is_final()
    {
        var z = await api.ZoneAsync();
        var saved = await z.RecordedAsync(7m, 20m);
        Assert.Equal(HttpStatusCode.OK, (await z.Owner.VoidAsync(saved.Id())).StatusCode);

        var voidAgain = await z.Owner.VoidAsync(saved.Id());
        var correctVoided = await z.Owner.CorrectAsync(saved.Id(), 7.1m, 20m);
        var invalidOnVoided = await z.Owner.CorrectAsync(saved.Id(), 99m, 20m, reason: null);

        Assert.Equal("Water measurement is Voided and cannot be used for this action.",
            (await voidAgain.Content.ReadFromJsonAsync<JsonElement>()).Text("detail"));
        foreach (var response in new[] { voidAgain, correctVoided, invalidOnVoided })
        {
            Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
            Assert.Equal("MSG-GEN-09", await response.ErrorCodeAsync());
        }
    }

    [Fact]
    public async Task Voiding_needs_a_reason_and_audits_the_values_before_and_after_once()
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
        Assert.Contains("\"Ph\":9.4", audit.Before);
        Assert.Contains("\"Ph\":9.4", audit.After);
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
        var future = await z.Owner.CorrectAsync(saved.Id(), 7.1m, 20m, measuredAt: DateTimeOffset.UtcNow.AddHours(1));

        Assert.Equal("Reason", (await noReason.Content.ReadFromJsonAsync<JsonElement>()).Text("field"));
        Assert.Equal("Reason", (await tooLong.Content.ReadFromJsonAsync<JsonElement>()).Text("field"));
        Assert.Equal("pH", (await badValue.Content.ReadFromJsonAsync<JsonElement>()).Text("field"));
        Assert.Equal("Salinity", (await overSalinity.Content.ReadFromJsonAsync<JsonElement>()).Text("field"));
        Assert.Equal("Temperature", (await noTemperature.Content.ReadFromJsonAsync<JsonElement>()).Text("field"));
        Assert.Equal("Measured at", (await future.Content.ReadFromJsonAsync<JsonElement>()).Text("field"));
        Assert.Equal(7m, await api.QueryAsync(db => db.WaterMeasurements.Where(m => m.Id == saved.Id()).Select(m => m.Ph).SingleAsync()));
        Assert.DoesNotContain(await api.AuditOfAsync(saved.Id()), e => e.Action == "WaterMeasurement.Corrected");
    }

    [Fact]
    public async Task Voided_measurement_stays_in_the_history_and_the_export_with_its_reason_and_leaves_the_chart()
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
        Assert.Equal("Đo nhầm cọc", history.GetProperty("items").EnumerateArray().Single().Text("voidReason"));
        Assert.Equal(0, chart.GetProperty("points").GetArrayLength());
        Assert.Equal("Đo nhầm cọc", workbook.Worksheet("Measurements").Row(2).Cell(7).GetString());
    }

    [Fact]
    public async Task The_editing_routes_and_fields_and_the_record_version_are_gone()
    {
        var z = await api.ZoneAsync();
        var saved = await z.RecordedAsync(7m, 20m);
        var editing = $"/api/water-measurements/{saved.Id()}/editing";

        var start = await z.Owner.PostAsync(editing, null);
        var stop = await z.Owner.DeleteAsync(editing);
        var detail = await z.Owner.GetFromJsonAsync<JsonElement>($"/api/water-measurements/{saved.Id()}");
        var staleVersion = await z.Owner.PostAsJsonAsync($"/api/water-measurements/{saved.Id()}/corrections", new
        {
            recordVersion = 1,
            rowVersion = "stale",
            raftId = z.Raft.Id,
            ph = 7.1m,
            salinity = 20m,
            temperature = 25m,
            measuredAt = DateTimeOffset.UtcNow.AddMinutes(-5),
            reason = "Sai số",
        });

        Assert.Equal(HttpStatusCode.NotFound, start.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, stop.StatusCode);
        Assert.False(detail.TryGetProperty("currentEditors", out _));
        Assert.False(detail.TryGetProperty("editingNotice", out _));
        Assert.False(detail.TryGetProperty("rowVersion", out _));
        Assert.Equal(HttpStatusCode.OK, staleVersion.StatusCode);
    }

    [Fact]
    public async Task Unknown_measurement_is_404()
    {
        var z = await api.ZoneAsync();

        var correct = await z.Owner.CorrectAsync(Guid.NewGuid(), 7m, 20m, raftId: z.Raft.Id);
        var voided = await z.Owner.VoidAsync(Guid.NewGuid());

        Assert.Equal(HttpStatusCode.NotFound, correct.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, voided.StatusCode);
    }
}
