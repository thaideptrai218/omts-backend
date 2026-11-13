using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using OMTS.Api.IntegrationTests.Farming;
using OMTS.Api.IntegrationTests.Infrastructure;
using OMTS.Domain.Entities.Sales;

namespace OMTS.Api.IntegrationTests.Sales;

/// <summary>A cooperative with a sale setup, the receiving bank account of the VietQR codes and the same account in the cash book, and an Accountant of all zones.</summary>
public sealed record SettlementWorld(AuthApiFactory Api, SaleSetup Sale, HttpClient Accountant, Guid BankAccountId) : IDisposable
{
    public void Dispose() => Api.Dispose();
}

/// <summary>DEBT-02.6 calls as the clients send them. Every invoice code and bank reference is fictional.</summary>
public static class CustomerPaymentSettlementTestKit
{
    private const string Confirmations = "/api/customer-payments/confirmations";

    private static int _codes;

    public static async Task<SettlementWorld> StartSettlementAsync()
    {
        var api = new AuthApiFactory();
        var sale = await api.SaleSetupAsync();
        await api.UseReceivingAccountAsync();
        var accountant = (await api.AllZonesAccountantAsync()).Client;
        return new SettlementWorld(api, sale, accountant, await accountant.BankAccountAsync());
    }

    /// <summary>An invoice code no sale has: the transfer that carries it is recorded without a customer.</summary>
    public static string NewUnknownCode() => $"HD9{Interlocked.Increment(ref _codes):D8}";

    /// <summary>A bank account of the cash book (fictional bank and number).</summary>
    public static async Task<Guid> BankAccountAsync(this HttpClient client, string number = VietQrSaleTestKit.AccountNumber)
    {
        var response = await client.PostAsJsonAsync("/api/cash-accounts", new { kind = "Bank", bankBin = VietQrSaleTestKit.BankBin, accountNumber = number, holderName = "HTX DEMO" });
        return (await response.ReadAsync(HttpStatusCode.Created)).Id();
    }

    /// <summary>Two pay-later invoices of the sale's customer: 300,000 (20 days old) and 600,000.</summary>
    public static async Task<(JsonElement Older, JsonElement Newer)> TwoInvoicesAsync(this SettlementWorld w)
    {
        var older = await w.Sale.PayLaterAsync(w.Sale.CustomerId, 10m);
        await w.Api.BackDateAsync(older.Id(), 20);
        return (older, await w.Sale.PayLaterAsync(w.Sale.CustomerId, 20m));
    }

    public static object ToCustomer(Guid paymentId, Guid customerId, params (Guid InvoiceId, decimal Amount)[] allocations) =>
        allocations.Length == 0
            ? new { id = paymentId, customerId }
            : (object)new { id = paymentId, customerId, allocations = allocations.Select(a => new { invoiceId = a.InvoiceId, amount = a.Amount }).ToList() };

    public static object PayBack(
        Guid paymentId, string method = "Cash", DateOnly? date = null, string? reference = null, string? note = null, bool confirmDuplicateBankReference = false, Guid? cashAccountId = null) =>
        new
        {
            id = paymentId,
            payBack = new { paymentDate = (date ?? PurchaseRequestTestKit.Today).ToString("yyyy-MM-dd"), method, bankReference = reference, note, confirmDuplicateBankReference, cashAccountId },
        };

    public static Task<HttpResponseMessage> SettleAsync(this HttpClient client, params object[] settle) => client.PostAsJsonAsync(Confirmations, new { settle });

    /// <summary>The list of DEBT-02.2 in one call.</summary>
    public static Task<HttpResponseMessage> ConfirmCancelSettleAsync(this HttpClient client, Guid[] confirm, (Guid Id, string Reason)[] cancel, object[] settle) =>
        client.PostAsJsonAsync(Confirmations, new { confirm, cancel = cancel.Select(c => new { id = c.Id, reason = c.Reason }), settle });

    /// <summary>A transfer the bank reported whose code matches no saved invoice: a payment without a customer, held Awaiting confirmation and marked.</summary>
    public static async Task<CustomerPayment> OrphanTransferAsync(this AuthApiFactory api, decimal amount, string? reference = null, DateTimeOffset? occurredAt = null)
    {
        var code = NewUnknownCode();
        using var bank = api.CreateClient();
        var response = await bank.PostAsJsonAsync(
            "/api/dev/payment-notifications", new { invoiceCode = code, amount, bankReference = reference ?? VietQrSaleTestKit.NewReference(), occurredAt });
        var result = Assert.Single((await response.ReadAsync(HttpStatusCode.OK)).GetProperty("transactions").EnumerateArray());
        Assert.Equal("WithoutCustomer", result.Outcome());
        return await api.PaymentOfCodeAsync(code);
    }

    public static Task<CustomerPayment> PaymentOfCodeAsync(this AuthApiFactory api, string transferCode) =>
        api.QueryAsync(db => db.CustomerPayments.AsNoTracking().SingleAsync(p => p.TransferInvoiceCode == transferCode));

    public static Task<CustomerPayment> PaymentByIdAsync(this AuthApiFactory api, Guid id) =>
        api.QueryAsync(db => db.CustomerPayments.AsNoTracking().SingleAsync(p => p.Id == id));

    /// <summary>The money paid back that settles this payment, the cancelled ones too.</summary>
    public static Task<List<CustomerPayment>> PaidBackForAsync(this AuthApiFactory api, Guid paymentId) =>
        api.QueryAsync(db => db.CustomerPayments.AsNoTracking().Where(p => p.PaidBackForPaymentId == paymentId).ToListAsync());

    /// <summary>The parts of a payment, by invoice code.</summary>
    public static Task<List<(string InvoiceCode, decimal Amount)>> AllocationsOfAsync(this AuthApiFactory api, Guid paymentId) =>
        api.QueryAsync(async db => (await db.CustomerPaymentAllocations.AsNoTracking().Where(a => a.PaymentId == paymentId).OrderBy(a => a.Invoice.InvoicedAt)
            .Select(a => new { a.Invoice.Code, a.Amount }).ToListAsync()).Select(a => (a.Code, a.Amount)).ToList());

    public static Task<int> AuditCountAsync(this AuthApiFactory api, Guid recordId, string action) =>
        api.QueryAsync(db => db.AuditEvents.CountAsync(e => e.RecordId == recordId && e.Action == action));

    public static Task<string?> AuditAfterAsync(this AuthApiFactory api, Guid recordId, string action) =>
        api.QueryAsync(db => db.AuditEvents.Where(e => e.RecordId == recordId && e.Action == action).Select(e => e.After).SingleAsync());

    /// <summary>The awaiting row of the payment, as DEBT-02.2 shows it.</summary>
    public static async Task<JsonElement> AwaitingRowAsync(this HttpClient client, Guid paymentId) =>
        (await client.AwaitingRowsAsync()).Single(r => r.Id() == paymentId);

    public static JsonElement Settled(this JsonElement answer) => Assert.Single(answer.GetProperty("settled").EnumerateArray());

    /// <summary>The cash book lines of a fund whose code is <paramref name="code"/>, cancelled ones included.</summary>
    public static async Task<List<JsonElement>> CashBookLinesAsync(this HttpClient client, string fund, string code)
    {
        var page = await (await client.GetAsync($"/api/cash-book?fund={fund}&search={code}&pageSize=100")).ReadAsync(HttpStatusCode.OK);
        return page.GetProperty("items").EnumerateArray().Select(i => i.Clone()).ToList();
    }
}
