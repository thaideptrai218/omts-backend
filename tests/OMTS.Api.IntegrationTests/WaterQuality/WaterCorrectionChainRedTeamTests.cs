using System.Net.Http.Json;
using System.Net;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using OMTS.Api.IntegrationTests.Infrastructure;
using OMTS.Domain.Constants;
using OMTS.Domain.Entities.Farming;

namespace OMTS.Api.IntegrationTests.WaterQuality;

/// <summary>
/// ENV-02 correction chain (QA red team): a stale <c>recordVersion</c> anywhere in the chain gets MSG06 with
/// the newest version, voids follow the same rule, the measurement point never changes with the zone, and a
/// voided latest version leaves nothing current in the history, chart or export.
/// </summary>
public class WaterCorrectionChainRedTeamTests(AuthApiFactory api) : IClassFixture<AuthApiFactory>
{
    [Fact]
    public async Task Client_still_on_version_two_of_a_three_version_chain_gets_MSG06_naming_version_three()
    {
        var z = await api.ZoneAsync();
        var (manager, _) = await api.StaffAsync(z.Zone, edit: true);
        var v1 = await z.RecordedAsync(9.1m, 20m);
        var v2 = await (await manager.CorrectAsync(v1.Id(), 7.5m, 20m, recordVersion: 1)).Content.ReadFromJsonAsync<JsonElement>();
        var v3Response = await z.Owner.CorrectAsync(v2.Id(), 7.6m, 20m, recordVersion: 2, reason: "Đo lại");
        var v3 = await v3Response.Content.ReadFromJsonAsync<JsonElement>();

        var stale = await manager.CorrectAsync(v2.Id(), 7.7m, 20m, recordVersion: 2);
        var problem = await stale.Content.ReadFromJsonAsync<JsonElement>();

        Assert.Equal(HttpStatusCode.Created, v3Response.StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, stale.StatusCode);
        Assert.Equal("MSG06", problem.Text("code"));
        Assert.StartsWith($"The data was just updated by {z.OwnerAccount.DisplayName} at ", problem.Text("detail"));
        Assert.Equal(v3.Id(), problem.GetProperty("latestVersion").Id());
        Assert.Equal(3, problem.GetProperty("latestVersion").GetProperty("recordVersion").GetInt32());
        Assert.Equal(3, await api.QueryAsync(db => db.WaterMeasurements.CountAsync(m => m.ZoneId == z.Zone.Id)));

        var oldest = await z.Owner.GetFromJsonAsync<JsonElement>($"/api/water-measurements/{v1.Id()}");
        var versions = oldest.GetProperty("versions").EnumerateArray().ToList();
        Assert.Equal([1, 2, 3], versions.Select(v => v.GetProperty("recordVersion").GetInt32()));
        Assert.Equal([v1.Id(), v2.Id(), v3.Id()], versions.Select(v => v.Id()));
        Assert.Equal(["Corrected", "Corrected", "Confirmed"], versions.Select(v => v.Text("status")));
        Assert.Equal(["Corrected", "None", "None"], versions.Select(v => v.Text("alertState")));
        Assert.Equal("Đo lại", versions[2].Text("correctionReason"));
        Assert.False(oldest.GetProperty("isLatestVersion").GetBoolean());
        Assert.True((await z.Owner.GetFromJsonAsync<JsonElement>($"/api/water-measurements/{v3.Id()}")).GetProperty("isLatestVersion").GetBoolean());
    }

    [Fact]
    public async Task Void_with_a_stale_version_after_a_correction_is_MSG06_with_the_newest_version_and_changes_nothing()
    {
        var z = await api.ZoneAsync();
        var v1 = await z.RecordedAsync(7m, 20m);
        var v2 = await (await z.Owner.CorrectAsync(v1.Id(), 7.2m, 20m, recordVersion: 1)).Content.ReadFromJsonAsync<JsonElement>();

        var voidOld = await z.Owner.VoidAsync(v1.Id(), recordVersion: 1);
        var voidNewStale = await z.Owner.VoidAsync(v2.Id(), recordVersion: 1);

        foreach (var response in new[] { voidOld, voidNewStale })
        {
            var problem = await response.Content.ReadFromJsonAsync<JsonElement>();
            Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
            Assert.Equal("MSG06", problem.Text("code"));
            Assert.Equal(v2.Id(), problem.GetProperty("latestVersion").Id());
        }

        var stored = await api.QueryAsync(db => db.WaterMeasurements.AsNoTracking().Where(m => m.ZoneId == z.Zone.Id).ToListAsync());
        Assert.Equal(StatusIds.WaterMeasurement.Superseded, stored.Single(m => m.Id == v1.Id()).StatusId);
        Assert.Equal(StatusIds.WaterMeasurement.Confirmed, stored.Single(m => m.Id == v2.Id()).StatusId);
        Assert.All(stored, m => Assert.Null(m.VoidReason));
        Assert.Empty((await api.AuditOfAsync(v1.Id())).Concat(await api.AuditOfAsync(v2.Id())).Where(e => e.Action == "WaterMeasurement.Voided"));
    }

    [Fact]
    public async Task Record_version_ahead_of_the_record_is_MSG01()
    {
        var z = await api.ZoneAsync();
        var confirmed = await z.RecordedAsync(7m, 20m);

        var correct = await z.Owner.CorrectAsync(confirmed.Id(), 7.1m, 20m, recordVersion: 2);
        var voided = await z.Owner.VoidAsync(confirmed.Id(), recordVersion: 5);

        foreach (var response in new[] { correct, voided })
        {
            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
            Assert.Equal("MSG-GEN-01", await response.ErrorCodeAsync());
            Assert.Equal("Record version", (await response.Content.ReadFromJsonAsync<JsonElement>()).Text("field"));
        }

        Assert.Equal(StatusIds.WaterMeasurement.Confirmed,
            await api.QueryAsync(db => db.WaterMeasurements.Where(m => m.Id == confirmed.Id()).Select(m => m.StatusId).SingleAsync()));
    }

    [Fact]
    public async Task Measurement_point_is_copied_once_a_later_zone_edit_does_not_rewrite_the_chain()
    {
        var z = await api.ZoneAsync();
        var v1 = await z.RecordedAsync(7m, 20m);
        await api.UpdateAsync<FarmingZone>(z.Zone.Id, zone => zone.WaterMeasurementPoint = "Cọc đo mới");

        var v2 = await (await z.Owner.CorrectAsync(v1.Id(), 7.2m, 20m, recordVersion: 1)).Content.ReadFromJsonAsync<JsonElement>();
        var fresh = await z.RecordedAsync(7.4m, 20m);
        var oldDetail = await z.Owner.GetFromJsonAsync<JsonElement>($"/api/water-measurements/{v1.Id()}");

        Assert.Equal("Cọc đo số 1", oldDetail.Text("measurementPoint"));
        Assert.Equal("Cọc đo số 1", v2.Text("measurementPoint"));
        Assert.Equal("Cọc đo mới", fresh.Text("measurementPoint"));
    }

    [Fact]
    public async Task Voided_latest_correction_leaves_nothing_current_in_history_chart_and_export()
    {
        var z = await api.ZoneAsync();
        var v1 = await z.RecordedAsync(9.1m, 20m);
        var v2 = await (await z.Owner.CorrectAsync(v1.Id(), 9.3m, 20m, recordVersion: 1)).Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("Active", v2.Text("alertState"));

        var voided = await z.Owner.VoidAsync(v2.Id(), recordVersion: 2, reason: "Đo nhầm cọc");
        var voidedBody = await voided.Content.ReadFromJsonAsync<JsonElement>();
        var history = await z.Owner.GetFromJsonAsync<JsonElement>(z.Base);
        var chart = await z.Owner.GetFromJsonAsync<JsonElement>($"{z.Base}/chart");
        var export = await z.Owner.GetAsync($"{z.Base}/export");
        using var workbook = new ClosedXML.Excel.XLWorkbook(await export.Content.ReadAsStreamAsync());

        Assert.Equal(HttpStatusCode.OK, voided.StatusCode);
        Assert.Equal("Voided", voidedBody.Text("alertState"));
        var versions = voidedBody.GetProperty("versions").EnumerateArray().ToList();
        Assert.Equal(["Corrected", "Voided"], versions.Select(v => v.Text("status")));
        Assert.Equal(["Corrected", "Voided"], versions.Select(v => v.Text("alertState")));
        Assert.Equal("Đo nhầm cọc", versions[1].Text("voidReason"));
        Assert.Equal(0, history.GetProperty("total").GetInt32());
        Assert.All(chart.GetProperty("periods").EnumerateArray(), p => Assert.True(p.GetProperty("missing").GetBoolean()));
        Assert.Single(workbook.Worksheet("Measurements").RowsUsed());
        // A voided version is final: it cannot be corrected or voided again.
        Assert.Equal("MSG-GEN-09", await (await z.Owner.CorrectAsync(v2.Id(), 7m, 20m, recordVersion: 2)).ErrorCodeAsync());
    }

    [Fact]
    public async Task Draft_of_a_zone_discontinued_since_cannot_be_confirmed_but_can_still_be_voided()
    {
        var z = await api.ZoneAsync();
        var draft = await z.RecordedAsync(9.4m, 20m, confirm: false);
        await api.UpdateAsync<FarmingZone>(z.Zone.Id, zone => zone.StatusId = StatusIds.Usage.Discontinued);

        var confirm = await z.Owner.PostAsJsonAsync($"/api/water-measurements/{draft.Id()}/confirm", new { });
        var voided = await z.Owner.VoidAsync(draft.Id(), 1, reason: "Vùng đã ngừng");

        Assert.Equal(HttpStatusCode.BadRequest, confirm.StatusCode);
        Assert.Equal("MSG-GEN-01", await confirm.ErrorCodeAsync());
        Assert.Equal("Zone", (await confirm.Content.ReadFromJsonAsync<JsonElement>()).Text("field"));
        Assert.Empty(await api.AlertsOfAsync(draft.Id()));
        Assert.Equal(HttpStatusCode.OK, voided.StatusCode);
        Assert.Equal("Voided", (await voided.Content.ReadFromJsonAsync<JsonElement>()).Text("status"));
    }

    [Fact]
    public async Task Voiding_clears_the_voiders_editing_indicator()
    {
        var z = await api.ZoneAsync();
        var (manager, _) = await api.StaffAsync(z.Zone, edit: true);
        var confirmed = await z.RecordedAsync(7m, 20m);
        await manager.PostAsync($"/api/water-measurements/{confirmed.Id()}/editing", null);
        var before = await z.Owner.GetFromJsonAsync<JsonElement>($"/api/water-measurements/{confirmed.Id()}");

        Assert.Equal(HttpStatusCode.OK, (await manager.VoidAsync(confirmed.Id(), 1)).StatusCode);

        var after = await z.Owner.GetFromJsonAsync<JsonElement>($"/api/water-measurements/{confirmed.Id()}");
        Assert.Single(before.GetProperty("currentEditors").EnumerateArray());
        Assert.Empty(after.GetProperty("currentEditors").EnumerateArray());
        Assert.Equal(JsonValueKind.Null, after.GetProperty("editingNotice").ValueKind);
    }
}
