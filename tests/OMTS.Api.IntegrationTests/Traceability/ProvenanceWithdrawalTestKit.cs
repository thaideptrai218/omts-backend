using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using OMTS.Api.IntegrationTests.Farming;
using OMTS.Application.Features.Notifications;
using OMTS.Domain.Entities.Platform;
using OMTS.Domain.Enums;
using static OMTS.Api.IntegrationTests.Sales.PurchaseRequestTestKit;

namespace OMTS.Api.IntegrationTests.Traceability;

/// <summary>TRACE-04 calls and what a withdrawal leaves behind: the lot, its audit row and the notices to the traders.</summary>
public static class ProvenanceWithdrawalTestKit
{
    public const string WithdrawnAction = "HarvestLot.Withdrawn";

    public static Task<HttpResponseMessage> GetWithdrawPreviewAsync(this HttpClient client, Guid lotId) => client.GetAsync($"/api/harvest-lots/{lotId}/withdraw-preview");

    public static async Task<JsonElement> WithdrawPreviewAsync(this HttpClient client, Guid lotId) => await (await client.GetWithdrawPreviewAsync(lotId)).ReadAsync(HttpStatusCode.OK);

    public static Task<HttpResponseMessage> PostWithdrawAsync(this HttpClient client, Guid lotId, string? reason = "Seed lot data in doubt") =>
        client.PostAsJsonAsync($"/api/harvest-lots/{lotId}/withdraw", new { reason });

    public static async Task<JsonElement> WithdrawnAsync(this HttpClient client, Guid lotId, string? reason = "Seed lot data in doubt") =>
        await (await client.PostWithdrawAsync(lotId, reason)).ReadAsync(HttpStatusCode.OK);

    public static Task<HttpResponseMessage> GetWithdrawalNoticesAsync(this HttpClient client, Guid lotId) => client.GetAsync($"/api/harvest-lots/{lotId}/withdrawal-notices");

    public static async Task<JsonElement> WithdrawalNoticesAsync(this HttpClient client, Guid lotId) =>
        await (await client.GetWithdrawalNoticesAsync(lotId)).ReadAsync(HttpStatusCode.OK);

    /// <summary>The withdrawal audit rows of the lot, oldest first.</summary>
    public static Task<List<AuditEvent>> WithdrawAuditAsync(this WebApplicationFactory<Program> api, Guid lotId) => api.DbAsync(db => db.AuditEvents.AsNoTracking()
        .Where(e => e.RecordId == lotId && e.Action == WithdrawnAction).OrderBy(e => e.OccurredAt).ToListAsync());

    /// <summary>The sendings of the withdrawal notices of the lot (SMS rows), oldest first.</summary>
    public static Task<List<Notification>> NoticeSendingsAsync(this WebApplicationFactory<Program> api, Guid lotId) => api.DbAsync(db => db.Notifications.AsNoTracking()
        .Where(n => n.RelatedRecordId == lotId && n.Type == NotificationTypes.ProvenanceWithdrawn && n.Channel != NotificationChannel.InApp).OrderBy(n => n.CreatedAt).ToListAsync());

    /// <summary>The list rows of the withdrawal notices of a trader account.</summary>
    public static Task<List<Notification>> NoticesInListAsync(this WebApplicationFactory<Program> api, Guid accountId) => api.DbAsync(db => db.Notifications.AsNoTracking()
        .Where(n => n.RecipientAccountId == accountId && n.Type == NotificationTypes.ProvenanceWithdrawn && n.Channel == NotificationChannel.InApp).ToListAsync());

    /// <summary>Each trader of a preview or each notice of an answer as "name: HD000001, HD000002".</summary>
    public static List<string> Traders(this JsonElement answer, string property) => answer.GetProperty(property).EnumerateArray()
        .Select(t => $"{t.Text("customerName")}: {string.Join(", ", t.GetProperty("invoiceCodes").EnumerateArray().Select(c => c.GetString()))}")
        .ToList();

    /// <summary>The notice of the answer for this customer profile.</summary>
    public static JsonElement NoticeOf(this JsonElement answer, Guid customerId) =>
        answer.GetProperty("notices").EnumerateArray().Single(n => n.GetProperty("customerId").GetGuid() == customerId);

    public static List<string?> Codes(this JsonElement element, string property) => element.GetProperty(property).EnumerateArray().Select(c => c.GetString()).ToList();
}
