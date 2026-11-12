using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using OMTS.Api.IntegrationTests.Farming;
using OMTS.Api.IntegrationTests.Infrastructure;
using OMTS.Application.Features.Notifications;
using OMTS.Application.Features.Sales.Invoices;
using OMTS.Domain.Entities.Harvest;
using OMTS.Domain.Entities.Platform;
using OMTS.Domain.Entities.Sales;
using OMTS.Domain.Enums;
using static OMTS.Api.IntegrationTests.Sales.SalesInvoiceTestKit;

namespace OMTS.Api.IntegrationTests.Sales;

/// <summary>SALE-07 and SALE-08: the bodies as the clients send them, and what a correction or a cancellation leaves behind.</summary>
public static class SalesInvoiceChangeTestKit
{
    public const string Reason = "Weighed by mistake";

    public static Task<HttpResponseMessage> CorrectAsync(this HttpClient client, Guid invoiceId, object body) => client.PutAsJsonAsync($"/api/sales-invoices/{invoiceId}/correction", body);

    public static Task<HttpResponseMessage> CancelInvoiceAsync(this HttpClient client, Guid invoiceId, object body) => client.PostAsJsonAsync($"/api/sales-invoices/{invoiceId}/cancel", body);

    public static Dictionary<string, object?> PriceCut(JsonElement invoice, decimal amount) => new()
    {
        ["kind"] = "PriceCutAfterClaim",
        ["reason"] = Reason,
        ["rowVersion"] = invoice.Text("rowVersion"),
        ["priceCutAmount"] = amount,
    };

    public static Dictionary<string, object?> DataEntry(JsonElement invoice, object[] lines, params Guid[] notSoldWholeLotIds) => new()
    {
        ["kind"] = "DataEntryError",
        ["reason"] = Reason,
        ["rowVersion"] = invoice.Text("rowVersion"),
        ["lines"] = lines,
        ["notSoldWholeLotIds"] = notSoldWholeLotIds,
    };

    public static Dictionary<string, object?> Cancellation(JsonElement invoice) => new()
    {
        ["reason"] = "The trader never took the goods",
        ["rowVersion"] = invoice.Text("rowVersion"),
    };

    public static object GivenBack(Guid paymentId, bool givenBack) => new[] { new { paymentId, givenBack } };

    public static async Task<JsonElement> CorrectedAsync(this HttpClient client, Guid invoiceId, object body) =>
        await (await client.CorrectAsync(invoiceId, body)).ReadAsync(HttpStatusCode.OK);

    public static async Task<JsonElement> CancelledAsync(this HttpClient client, Guid invoiceId, object body) =>
        await (await client.CancelInvoiceAsync(invoiceId, body)).ReadAsync(HttpStatusCode.OK);

    public static Task<HarvestLot> LotOfAsync(this AuthApiFactory api, Guid lotId) => api.QueryAsync(db => db.HarvestLots.AsNoTracking().SingleAsync(l => l.Id == lotId));

    public static Task<List<AuditEvent>> InvoiceAuditAsync(this AuthApiFactory api, Guid invoiceId, string action) =>
        api.QueryAsync(db => db.AuditEvents.AsNoTracking().Where(e => e.RecordId == invoiceId && e.Action == action).ToListAsync());

    public static Task<List<Notification>> OwedBackNoticesAsync(this AuthApiFactory api, Guid invoiceId) =>
        api.QueryAsync(db => db.Notifications.AsNoTracking()
            .Where(n => n.RelatedRecordId == invoiceId && n.Type == NotificationTypes.CustomerMoneyOwedBack && n.Channel == NotificationChannel.InApp).ToListAsync());

    public static Task<List<Notification>> CancellationNoticesAsync(this AuthApiFactory api, Guid invoiceId) =>
        api.QueryAsync(db => db.Notifications.AsNoTracking().Where(n => n.RelatedRecordId == invoiceId && n.Type == SalesInvoiceMessages.CancellationType).ToListAsync());

    public static Task<string> PhoneOfAsync(this AuthApiFactory api, Guid customerId) =>
        api.QueryAsync(db => db.Customers.AsNoTracking().Where(c => c.Id == customerId).Select(c => c.PhoneNumber!).SingleAsync());

    /// <summary>The invoice of <paramref name="kg"/> kg at the unit price, on the debt, sold by the Owner; the lot closes unless <paramref name="notSoldWhole"/>.</summary>
    public static async Task<JsonElement> SoldOnDebtAsync(this SaleSetup sale, Guid lotId, decimal kg, decimal unitPrice, bool notSoldWhole = true, Guid? customerId = null) =>
        (await sale.Owner.Client.SellAsync(Invoice(
            customerId ?? sale.CustomerId, [Line(sale.ProductId, lotId, kg, unitPrice)], Cash(0m, "PayLater", payLaterOwnerAgreed: true), notSoldWhole ? [lotId] : []))).GetProperty("invoice");
}
