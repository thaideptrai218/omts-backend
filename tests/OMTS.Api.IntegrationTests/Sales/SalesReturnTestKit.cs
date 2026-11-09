using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using OMTS.Api.IntegrationTests.Farming;
using OMTS.Api.IntegrationTests.Infrastructure;
using OMTS.Application.Features.Notifications;
using OMTS.Domain.Entities.Platform;
using OMTS.Domain.Entities.Sales;
using OMTS.Domain.Enums;

namespace OMTS.Api.IntegrationTests.Sales;

/// <summary>CLAIM-03 calls: the bodies as the clients send them and what a return leaves behind.</summary>
public static class SalesReturnTestKit
{
    public const string ReturnReason = "Oysters arrived dead";

    public static Task<HttpResponseMessage> PreviewReturnAsync(this HttpClient client, Guid invoiceId, Guid? customerId = null) =>
        client.GetAsync($"/api/sales-invoices/{invoiceId}/sales-return/preview{(customerId is null ? string.Empty : $"?customerId={customerId}")}");

    public static Task<HttpResponseMessage> PostReturnAsync(this HttpClient client, Guid invoiceId, object body) => client.PostAsJsonAsync($"/api/sales-invoices/{invoiceId}/sales-return", body);

    public static Task<HttpResponseMessage> CancelSalesReturnAsync(this HttpClient client, Guid returnId, object body) => client.PostAsJsonAsync($"/api/sales-returns/{returnId}/cancel", body);

    public static Task<HttpResponseMessage> GetSalesReturnAsync(this HttpClient client, Guid returnId) => client.GetAsync($"/api/sales-returns/{returnId}");

    /// <summary>A body taking back every line of <paramref name="invoice"/>; one outcome per line in order, the last repeated.</summary>
    public static Dictionary<string, object?> ReturnBody(JsonElement invoice, params string[] outcomes)
    {
        var lines = invoice.GetProperty("lines").EnumerateArray().Select((l, i) => new { invoiceLineId = l.Id(), outcome = outcomes[Math.Min(i, outcomes.Length - 1)] }).ToList();
        return new Dictionary<string, object?> { ["reason"] = ReturnReason, ["lines"] = lines, ["rowVersion"] = invoice.Text("rowVersion") };
    }

    public static async Task<JsonElement> ReturnedAsync(this HttpClient client, Guid invoiceId, object body) =>
        await (await client.PostReturnAsync(invoiceId, body)).ReadAsync(HttpStatusCode.Created);

    public static Dictionary<string, object?> CancelBody(JsonElement salesReturn, string? reason = "Recorded by mistake") => new()
    {
        ["reason"] = reason,
        ["rowVersion"] = salesReturn.Text("rowVersion"),
    };

    public static Task<List<Notification>> ReturnNoticesAsync(this AuthApiFactory api, Guid invoiceId) =>
        api.QueryAsync(db => db.Notifications.AsNoTracking()
            .Where(n => n.RelatedRecordId == invoiceId && n.Channel == NotificationChannel.InApp
                && (n.Type == NotificationTypes.SalesReturnRecorded || n.Type == NotificationTypes.CustomerMoneyOwedBack)).ToListAsync());

    public static Task<List<SalesReturn>> ReturnsOfAsync(this AuthApiFactory api, Guid invoiceId) =>
        api.QueryAsync(db => db.SalesReturns.AsNoTracking().Include(r => r.Lines).Where(r => r.InvoiceId == invoiceId).ToListAsync());
}
