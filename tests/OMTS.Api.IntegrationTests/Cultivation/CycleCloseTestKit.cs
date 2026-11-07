using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using OMTS.Api.IntegrationTests.Farming;
using OMTS.Api.IntegrationTests.Infrastructure;
using OMTS.Domain.Entities.Platform;

namespace OMTS.Api.IntegrationTests.Cultivation;

/// <summary>CYCLE-06 calls (close preview and close) and the CYCLE-05 summary of a cycle.</summary>
public static class CycleCloseTestKit
{
    public const string Assessment = "Oysters grew well; two lanes were slow.";
    public const string ClosedType = "CYCLE_CLOSED";
    public const string ClosedAction = "CultivationCycle.Closed";

    public static Task<HttpResponseMessage> GetClosePreviewAsync(this HttpClient client, Guid cycleId) =>
        client.GetAsync($"/api/cultivation-cycles/{cycleId}/close/preview");

    public static async Task<JsonElement> ClosePreviewAsync(this HttpClient client, Guid cycleId) =>
        await (await client.GetClosePreviewAsync(cycleId)).ReadAsync(HttpStatusCode.OK);

    public static Task<HttpResponseMessage> PostCloseAsync(this HttpClient client, Guid cycleId, string? assessment, string? rowVersion, bool ownerApproved = false) =>
        client.PostAsJsonAsync($"/api/cultivation-cycles/{cycleId}/close", new { assessment, rowVersion, ownerApproved });

    /// <summary>Sends a close with the version of a fresh preview.</summary>
    public static async Task<HttpResponseMessage> SendCloseAsync(this HttpClient client, Guid cycleId, string? assessment = Assessment, bool ownerApproved = false)
    {
        var version = (await client.ClosePreviewAsync(cycleId)).Text("cycleRowVersion");
        return await client.PostCloseAsync(cycleId, assessment, version, ownerApproved);
    }

    /// <summary>A close that must be saved: the whole answer, the cycle and its summary.</summary>
    public static async Task<JsonElement> CloseCycleAsync(this HttpClient client, Guid cycleId, string? assessment = Assessment, bool ownerApproved = false) =>
        await (await client.SendCloseAsync(cycleId, assessment, ownerApproved)).ReadAsync(HttpStatusCode.OK);

    public static Task<JsonElement> CycleSummaryAsync(this HttpClient client, Guid cycleId) =>
        client.GetFromJsonAsync<JsonElement>($"/api/cultivation-cycles/{cycleId}/summary");

    public static Task<List<Notification>> ClosedNoticesAsync(this AuthApiFactory factory, Guid cycleId) =>
        factory.QueryAsync(db => db.Notifications.Where(n => n.Type == ClosedType && n.RelatedRecordId == cycleId).ToListAsync());

    public static Task<List<string?>> CloseAuditAftersAsync(this AuthApiFactory factory, Guid cycleId) =>
        factory.QueryAsync(db => db.AuditEvents.Where(e => e.Action == ClosedAction && e.RecordId == cycleId).Select(e => e.After).ToListAsync());

    public static decimal Number(this JsonElement element, string property) => element.GetProperty(property).GetDecimal();
}
