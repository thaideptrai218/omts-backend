using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using OMTS.Api.IntegrationTests.Farming;
using OMTS.Api.IntegrationTests.Infrastructure;
using OMTS.Application.Features.Sales.Invoices;
using OMTS.Domain.Entities.Harvest;
using OMTS.Domain.Entities.Identity;
using OMTS.Domain.Entities.Platform;
using OMTS.Infrastructure.Messaging;
using OMTS.Infrastructure.Messaging.Fakes;
using static OMTS.Api.IntegrationTests.Sales.SalesInvoiceTestKit;

namespace OMTS.Api.IntegrationTests.Sales;

/// <summary>SALE-06 and the end of the sale: the calls, the sendings kept for an invoice and the SMS the fake sender received.</summary>
public static class SalesInvoiceSendingTestKit
{
    /// <summary>A receiving bank account for the tests: never a real one.</summary>
    public const string DemoAccountNumber = "999900000001";

    public const string DemoBankName = "DEMOBANK";

    public static Task<HttpResponseMessage> FinishAsync(this HttpClient client, Guid invoiceId, bool? wantsQrCode) =>
        client.PostAsJsonAsync($"/api/sales-invoices/{invoiceId}/finish", new { wantsQrCode });

    public static Task<HttpResponseMessage> ResendAsync(this HttpClient client, Guid invoiceId) =>
        client.PostAsync($"/api/sales-invoices/{invoiceId}/resend", null);

    /// <summary>Saves a sale of <paramref name="kg"/> kg of the lot at the list price, paid in cash, and answers the invoice.</summary>
    public static async Task<JsonElement> SoldAsync(this HttpClient client, SaleSetup sale, Guid lotId, decimal kg = 10m, Guid? customerId = null, bool walkIn = false) =>
        (await client.SellAsync(Invoice(walkIn ? null : customerId ?? sale.CustomerId, [Line(sale.ProductId, lotId, kg)], Cash(kg * ListPrice), lotId))).GetProperty("invoice");

    public static Task PublishAsync(this AuthApiFactory api, Guid lotId) =>
        api.UpdateAsync<HarvestLot>(lotId, l => l.PublishedAt = DateTimeOffset.UtcNow.AddMinutes(-5));

    public static Task WithdrawAsync(this AuthApiFactory api, Guid lotId) =>
        api.UpdateAsync<HarvestLot>(lotId, l => (l.PublishedAt, l.WithdrawnAt, l.WithdrawalReason) = (DateTimeOffset.UtcNow.AddDays(-2), DateTimeOffset.UtcNow.AddDays(-1), "Reason"));

    public static async Task SetReceivingAccountAsync(this AuthApiFactory api)
    {
        var id = await api.QueryAsync(db => db.Cooperatives.Select(c => c.Id).FirstAsync());
        await api.UpdateAsync<Cooperative>(id, c => c.BankAccount = new ReceivingBankAccount
        {
            BankBin = "970000", BankName = DemoBankName, AccountNumber = DemoAccountNumber, AccountHolderName = "DEMO HOLDER", AcceptedAt = DateTimeOffset.UtcNow,
        });
    }

    /// <summary>The sendings of the invoice message, oldest first.</summary>
    public static Task<List<Notification>> InvoiceSendingsAsync(this WebApplicationFactory<Program> api, Guid invoiceId) =>
        api.DbAsync(db => db.Notifications.AsNoTracking()
            .Where(n => n.RelatedRecordId == invoiceId && n.Type == SalesInvoiceMessages.MessageType).OrderBy(n => n.CreatedAt).ToListAsync());

    /// <summary>The SMS the fake sender kept for this number, newest first.</summary>
    public static IReadOnlyList<FakeMessage> SmsTo(this WebApplicationFactory<Program> api, string phoneNumber) =>
        api.Services.GetRequiredService<FakeSmsSender>().Inbox.List(phoneNumber);

    /// <summary>Undoes <see cref="PurchaseRequestTestKit.RefuseSmsTo"/>: the fake sender accepts this number again.</summary>
    public static void AllowSmsTo(this WebApplicationFactory<Program> api, string phoneNumber) =>
        api.Services.GetRequiredService<IOptions<SmsOptions>>().Value.Fake.FailFor.Remove(phoneNumber);

    public static async Task<JsonElement> FinishedAsync(this HttpClient client, Guid invoiceId, bool wantsQrCode) =>
        await (await client.FinishAsync(invoiceId, wantsQrCode)).ReadAsync(HttpStatusCode.OK);
}
