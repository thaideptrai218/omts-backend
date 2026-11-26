using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using OMTS.Api.IntegrationTests.Infrastructure;
using OMTS.Domain.Entities.Farming;

namespace OMTS.Api.IntegrationTests.WaterQuality;

/// <summary>
/// WATER-02 on one record: a void is final, needs a reason and is audited with the values before and after; a measurement is
/// never corrected. Who may void: <see cref="WaterCorrectionAccessTests"/>; who is told: <see cref="WaterVoidNoticeTests"/>;
/// parallel saves: <see cref="WaterCorrectionConflictTests"/>.
/// </summary>
public class WaterCorrectionTests(AuthApiFactory api) : IClassFixture<AuthApiFactory>
{
    [Fact]
    public async Task A_voided_record_is_final()
    {
        var z = await api.ZoneAsync();
        var saved = await z.RecordedAsync(7m, 20m);
        Assert.Equal(HttpStatusCode.OK, (await z.Owner.VoidAsync(saved.Id())).StatusCode);

        var voidAgain = await z.Owner.VoidAsync(saved.Id());
        var invalidOnVoided = await z.Owner.VoidAsync(saved.Id(), reason: null);

        Assert.Equal("Water measurement is Voided and cannot be used for this action.",
            (await voidAgain.Content.ReadFromJsonAsync<JsonElement>()).Text("detail"));
        foreach (var response in new[] { voidAgain, invalidOnVoided })
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
    public async Task Voided_measurement_stays_in_the_history_and_the_export_with_its_reason_and_leaves_the_chart()
    {
        var z = await api.ZoneAsync();
        var saved = await z.RecordedAsync(9.1m, 20m);

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
        var staleVersion = await z.Owner.PostAsJsonAsync($"/api/water-measurements/{saved.Id()}/void", new
        {
            recordVersion = 1,
            rowVersion = "stale",
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

        var voided = await z.Owner.VoidAsync(Guid.NewGuid());

        Assert.Equal(HttpStatusCode.NotFound, voided.StatusCode);
    }
}
