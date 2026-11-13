using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using OMTS.Api.IntegrationTests.Farming;
using OMTS.Api.IntegrationTests.Harvest;
using OMTS.Api.IntegrationTests.Infrastructure;
using OMTS.Application.Common.Interfaces.Payments;
using OMTS.Application.Common.Interfaces.Persistence;
using OMTS.Application.Features.Harvest;
using OMTS.Domain.Constants;
using OMTS.Domain.Entities.Sales;
using OMTS.Domain.Enums;
using OMTS.Infrastructure.Payments;
using static OMTS.Api.IntegrationTests.Sales.SalesInvoiceTestKit;
using static OMTS.Api.IntegrationTests.Sales.VietQrSaleTestKit;

namespace OMTS.Api.IntegrationTests.Sales;

/// <summary>
/// QA of PR 7 (SALE-05 steps 7-8, SALE-05.0.E4, GFR-08, API-13): the webhook's way in, what a transfer becomes at the edges of the amount, and the states a sale can be
/// left in while its transfer is awaited. Tests that were named Defect_ showed a defect; they pass since the fixes and keep their assertions.
/// </summary>
public class VietQrWebhookQaTests
{
    private static async Task<(SaleSetup Sale, string Code)> AwaitingSaleAsync(AuthApiFactory api, Guid? customer = null, decimal kg = 10m, decimal cash = 0m)
    {
        var sale = await api.SaleSetupAsync();
        await api.UseReceivingAccountAsync();
        var code = (await sale.Manager.Client.AwaitTransferAsync(Invoice(customer ?? sale.CustomerId, [Line(sale.ProductId, sale.LotId, kg)], VietQr(cash)))).Code();
        return (sale, code);
    }

    private static byte[] Body(params object[] transactions) => Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new { transactions }));

    private static object Transfer(string reference, object amount, string content, string? occurredAt = null) =>
        new { bankReference = reference, accountNumber = AccountNumber, amount, content, occurredAt = occurredAt ?? DateTimeOffset.UtcNow.ToString("O"), direction = "In" };

    private static async Task<List<JsonElement>> ResultsAsync(HttpResponseMessage response) =>
        (await response.ReadAsync(HttpStatusCode.OK)).GetProperty("transactions").EnumerateArray().ToList();

    private static async Task<HttpResponseMessage> PostSignedAsync(HttpClient bank, byte[] body, string signature, string header = FakeBankNotificationParser.SignatureHeader)
    {
        using var content = new ByteArrayContent(body);
        content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/json");
        content.Headers.TryAddWithoutValidation(header, signature);
        return await bank.PostAsync(WebhookRoute, content);
    }

    // API-13: the signature covers the exact raw bytes; the header name in any case; the hex in any case; nothing else gets in.
    [Fact]
    public async Task The_signature_covers_the_exact_raw_body_and_only_a_signed_notification_is_read()
    {
        using var api = new AuthApiFactory();
        var (_, code) = await AwaitingSaleAsync(api);
        var secret = NewSecret();
        using var hooked = api.WithWebhookSecret(secret);
        using var bank = hooked.CreateClient();
        var body = Body(Transfer(NewReference(), 300_000m, code));
        var signature = FakeBankNotificationParser.Sign(body, secret);

        // The same JSON with one more space is another body: the service signs bytes, not meaning.
        var spaced = body.Concat(Encoding.UTF8.GetBytes(" ")).ToArray();
        Assert.Equal(HttpStatusCode.Unauthorized, (await PostSignedAsync(bank, spaced, signature)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await PostSignedAsync(bank, body, FakeBankNotificationParser.SignaturePrefix)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await PostSignedAsync(bank, body, signature[FakeBankNotificationParser.SignaturePrefix.Length..])).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await PostSignedAsync(bank, body, FakeBankNotificationParser.Sign(body, secret + " "))).StatusCode);
        var refused = await PostSignedAsync(bank, body, FakeBankNotificationParser.Sign(body, NewSecret()));
        Assert.Equal(HttpStatusCode.Unauthorized, refused.StatusCode);
        Assert.DoesNotContain(code, await refused.Content.ReadAsStringAsync());
        Assert.Equal(0, await api.QueryAsync(db => db.CustomerPayments.CountAsync()));

        // Header names are case-insensitive in HTTP; the hex may come in upper case.
        var upper = FakeBankNotificationParser.SignaturePrefix + signature[FakeBankNotificationParser.SignaturePrefix.Length..].ToUpperInvariant();
        var read = await ResultsAsync(await PostSignedAsync(bank, body, upper, FakeBankNotificationParser.SignatureHeader.ToLowerInvariant()));
        Assert.Equal("CompletedSale", Assert.Single(read).Outcome());

        // Replayed as it was sent (same signature, same body): recorded once (AC-07).
        Assert.Equal("AlreadyRecorded", Assert.Single(await ResultsAsync(await PostSignedAsync(bank, body, signature))).Outcome());
        Assert.Equal(1, await api.QueryAsync(db => db.CustomerPayments.CountAsync(p => p.Method == CustomerPaymentMethod.VietQrInvoice)));
    }

    // Payments:Provider = Fake works in Development only: elsewhere the parser accepts nothing (401) and the dev simulate endpoint does not exist (404).
    [Fact]
    public async Task Outside_development_the_fake_service_reads_no_notification_and_the_simulate_endpoint_is_404()
    {
        using var api = new AuthApiFactory();
        var secret = NewSecret();
        await using var production = api.WithWebHostBuilder(b => b.UseEnvironment("Production")
            .UseSetting("Ocr:Provider", string.Empty)
            .UseSetting("Payments:Provider", "Fake")
            .UseSetting(SecretKey, secret));
        using var bank = production.CreateClient();
        var body = Body(Transfer(NewReference(), 300_000m, "HD000001"));

        Assert.IsType<UnconfiguredBankNotificationParser>(production.Services.GetRequiredService<IBankNotificationParser>());
        Assert.Equal(HttpStatusCode.Unauthorized, (await bank.NotifyAsync(body, secret)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await bank.PostAsJsonAsync("/api/dev/payment-notifications", new { invoiceCode = "HD000001", amount = 1_000m })).StatusCode);
        Assert.Equal(0, await api.QueryAsync(db => db.CustomerPayments.CountAsync()));
    }

    // A signed body is read whole or not at all: 1 to 100 transfers, each with amount, time and direction; amounts beyond what VND allows are not recorded.
    [Fact]
    public async Task A_signed_notification_is_read_whole_or_refused_and_amounts_out_of_range_are_never_recorded()
    {
        using var api = new AuthApiFactory();
        var (_, code) = await AwaitingSaleAsync(api);
        var secret = NewSecret();
        using var hooked = api.WithWebhookSecret(secret);
        using var bank = hooked.CreateClient();

        var good = Transfer(NewReference(), 300_000m, code);
        var noAmount = new { bankReference = NewReference(), accountNumber = AccountNumber, content = code, occurredAt = DateTimeOffset.UtcNow, direction = "In" };
        Assert.Equal(HttpStatusCode.BadRequest, (await bank.NotifyAsync(Body(good, noAmount), secret)).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await bank.NotifyAsync(Body(Enumerable.Range(0, 101).Select(_ => good).ToArray()), secret)).StatusCode);
        var tooBig = Encoding.UTF8.GetBytes(
            $"{{\"transactions\":[{{\"bankReference\":\"{NewReference()}\",\"accountNumber\":\"{AccountNumber}\",\"amount\":1e30,\"content\":\"{code}\",\"occurredAt\":\"{DateTimeOffset.UtcNow:O}\",\"direction\":\"In\"}}]}}");
        Assert.Equal(HttpStatusCode.BadRequest, (await bank.NotifyAsync(tooBig, secret)).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await bank.NotifyAsync(Encoding.UTF8.GetBytes("null"), secret)).StatusCode);
        Assert.Equal(0, await api.QueryAsync(db => db.CustomerPayments.CountAsync()));

        var results = await ResultsAsync(await bank.NotifyAsync(Body(
            Transfer(NewReference(), 10_000_000_000_000_000_000m, code),
            Transfer(NewReference(), -300_000m, code),
            Transfer(NewReference(), 0m, code),
            Transfer(NewReference(), 299_999.5m, code)), secret));
        Assert.All(results, r => Assert.Equal(("Ignored", "Invalid"), (r.Outcome(), r.Text("reason"))));
        Assert.Equal(0, await api.QueryAsync(db => db.CustomerPayments.CountAsync()));
        Assert.Equal(PendingSaleStatus.Awaiting, (await api.PendingRowAsync(code)).Status);
    }

    // GFR-08 "not recorded again": the same reference twice in one notification, and with spaces around it, is one transfer.
    [Fact]
    public async Task The_same_reference_twice_in_one_notification_or_with_spaces_around_it_is_recorded_once()
    {
        using var api = new AuthApiFactory();
        var (_, code) = await AwaitingSaleAsync(api);
        var secret = NewSecret();
        using var hooked = api.WithWebhookSecret(secret);
        using var bank = hooked.CreateClient();
        var reference = NewReference();

        var results = await ResultsAsync(await bank.NotifyAsync(Body(Transfer(reference, 300_000m, code), Transfer($"  {reference} ", 300_000m, code)), secret));

        Assert.Equal(["CompletedSale", "AlreadyRecorded"], results.Select(r => r.Outcome()).ToList());
        Assert.Equal([reference, reference], results.Select(r => r.Text("bankReference")).ToList());
        Assert.Single(await api.VietQrPaymentsAsync(reference));
    }

    // AC-07 at the 1 VND edges: 1 VND short waits; exactly the amount completes; a second full transfer pays the invoice again (money owed back); the short one keeps
    // its code and stays without a customer for the Accountant (DEBT-02.6), so nothing is lost before settling is built.
    [Fact]
    public async Task One_vnd_short_waits_the_exact_amount_completes_and_a_second_transfer_pays_the_saved_invoice()
    {
        using var api = new AuthApiFactory();
        var (sale, code) = await AwaitingSaleAsync(api);
        var client = sale.Manager.Client;
        var shortReference = NewReference();

        Assert.Equal("WithoutCustomer", (await api.TransferAsync(code, 299_999m, shortReference)).Outcome());
        var polled = await client.PolledAsync(code);
        Assert.Equal(("Awaiting", 299_999m), (polled.Text("status"), polled.Money("receivedAmount")));

        Assert.Equal("CompletedSale", (await api.TransferAsync(code, 300_000m)).Outcome());
        Assert.Equal("PaidInvoice", (await api.TransferAsync(code.ToLowerInvariant(), 300_000m)).Outcome());

        var done = await client.PolledAsync(code);
        Assert.Equal(("Completed", 0m), (done.Text("status"), done.Money("receivedAmount")));
        Assert.Equal($"Invoice {code} is completed: 300,000 VND received by VietQR.", done.GetProperty("invoice").Text("message"));
        Assert.Equal(-300_000m, await client.CustomerDebtAsync(sale.CustomerId));
        var invoiceId = done.GetProperty("invoiceId").GetGuid();
        Assert.Equal([300_000m, 300_000m], (await api.PaymentsOfAsync(invoiceId)).Select(p => p.Amount).ToList());
        var left = Assert.Single(await api.VietQrPaymentsAsync(shortReference));
        Assert.Equal(((Guid?)null, (Guid?)null, code, StatusIds.CustomerPayment.AwaitingConfirmation), (left.CustomerId, left.InvoiceId, left.TransferInvoiceCode, left.StatusId));
    }

    // Observation (L, PM): two transfers that together pay the code never complete the sale; each is short on its own, and the poll shows the full amount received.
    [Fact]
    public async Task Observed_two_short_transfers_that_together_pay_the_amount_leave_the_sale_awaiting()
    {
        using var api = new AuthApiFactory();
        var (sale, code) = await AwaitingSaleAsync(api);

        Assert.Equal("WithoutCustomer", (await api.TransferAsync(code, 150_000m)).Outcome());
        Assert.Equal("WithoutCustomer", (await api.TransferAsync(code, 150_000m)).Outcome());

        var polled = await sale.Manager.Client.PolledAsync(code);
        Assert.Equal(("Awaiting", 300_000m, 300_000m), (polled.Text("status"), polled.Money("receivedAmount"), polled.Money("toPayAmount")));
    }

    // H1 – GFR-08 "the system records a customer payment": the lot of a sale awaiting its transfer cannot be removed (HARVEST-01.1, fix (a)); a lot gone anyway (removed before
    // that rule, by hand) must still let the transfer be recorded, not answered 500 (the service retries it for ever and the money is in no list) (fix (b)).
    // Changed with the fix: the removal through the API is now refused, so the lot is removed in the database to reach fix (b); the original assertions are kept.
    [Fact]
    public async Task A_lot_of_a_sale_awaiting_its_transfer_is_not_removed_and_a_transfer_for_a_sale_that_can_no_longer_be_saved_is_still_recorded()
    {
        using var api = new AuthApiFactory();
        var (sale, code) = await AwaitingSaleAsync(api);
        var refused = await (await api.SendRemovalAsync(sale.Owner.Client, sale.LotId)).ReadAsync(HttpStatusCode.Conflict);
        Assert.Equal("MSG-GEN-09", refused.Text("code"));
        await api.UseDbAsync(db => db.HarvestLots.Remove(db.HarvestLots.Single(l => l.Id == sale.LotId)));
        var reference = NewReference();

        using var client = api.CreateClient();
        var response = await client.PostAsJsonAsync("/api/dev/payment-notifications", new { invoiceCode = code, amount = 300_000m, bankReference = reference });

        Assert.True(response.StatusCode == HttpStatusCode.OK, $"the notification answered {(int)response.StatusCode}: {await response.Content.ReadAsStringAsync()}");
        Assert.Single(await api.VietQrPaymentsAsync(reference));
        var payment = Assert.Single(await api.VietQrPaymentsAsync(reference));
        Assert.Equal(((Guid?)null, (Guid?)null, code, 300_000m), (payment.CustomerId, payment.InvoiceId, payment.TransferInvoiceCode, payment.Amount));
        Assert.Equal(PendingSaleStatus.Stopped, (await api.PendingRowAsync(code)).Status);
        Assert.Equal(0, await api.QueryAsync(db => db.SalesInvoices.CountAsync()));
    }

    // M1 – GFR-08 "A transfer whose bank reference is already recorded is not recorded again": the Accountant recorded the transfer from the bank statement (BR-DEBT-01/03,
    // e.g. while the service was down); the late notification of the same transfer must not lower the debt a second time.
    [Fact]
    public async Task A_transfer_already_recorded_by_the_accountant_with_its_bank_reference_is_not_recorded_again()
    {
        using var api = new AuthApiFactory();
        var sale = await api.SaleSetupAsync();
        await api.UseReceivingAccountAsync();
        var invoice = await sale.PayLaterAsync(sale.CustomerId, 10m);
        var accountant = await api.AllZonesAccountantAsync();
        var reference = NewReference();
        await accountant.Client.PaidAsync(CustomerPaymentTestKit.MoneyBody(sale.CustomerId, 300_000m).With("bankReference", reference));
        Assert.Equal(0m, await sale.Manager.Client.CustomerDebtAsync(sale.CustomerId));

        var late = await api.TransferAsync(invoice.Text("code"), 300_000m, reference);

        Assert.Equal("AlreadyRecorded", late.Outcome());
        Assert.Equal(0m, await sale.Manager.Client.CustomerDebtAsync(sale.CustomerId));
        Assert.Single(await api.VietQrPaymentsAsync(reference));
    }

    // M2 – SALE-05.0.E4 step 2: the profile of a sale awaiting its transfer is merged (SALE-04.3 moves the sale to the kept profile); the Zone Manager must still be able to
    // change the payment method. The snapshot keeps the merged profile, so the change is refused with MSG-GEN-09 and only Stop sale is left.
    [Fact]
    public async Task Changing_the_payment_of_a_sale_whose_customer_was_merged_meanwhile_saves_the_invoice_on_the_kept_profile()
    {
        using var api = new AuthApiFactory();
        var (sale, code) = await AwaitingSaleAsync(api);
        var trader = await api.TraderAsync();
        (await sale.Owner.Client.MergeAsync(trader.Profile.Id, sale.CustomerId, ownerApproved: true)).EnsureSuccessStatusCode();
        Assert.Equal(trader.Profile.Id, (await api.PendingRowAsync(code)).CustomerId);

        var changed = await sale.Manager.Client.ChangePaymentAsync(code, Cash(300_000m));

        var saved = await changed.ReadAsync(HttpStatusCode.Created);
        Assert.Equal(trader.Profile.Id, saved.GetProperty("invoice").GetProperty("customer").Id());
    }

    // M3 – DEBT-02.2 step 3 "A marked payment is settled (see DEBT-02.6)": a transfer whose code matches no saved invoice must be settled (attached to a profile or paid back),
    // which sets it Confirmed. Confirming it plainly takes it out of the list it is settled from and leaves it Confirmed in no customer debt.
    [Fact]
    public async Task A_transfer_without_a_customer_is_not_confirmed_without_being_settled()
    {
        using var api = new AuthApiFactory();
        var (sale, code) = await AwaitingSaleAsync(api);
        await (await sale.Manager.Client.StopAsync(code, confirmed: true)).ReadAsync(HttpStatusCode.OK);
        var reference = NewReference();
        Assert.Equal("WithoutCustomer", (await api.TransferAsync(code, 300_000m, reference)).Outcome());
        var payment = Assert.Single(await api.VietQrPaymentsAsync(reference));
        var accountant = await api.AllZonesAccountantAsync();

        var confirmed = await accountant.Client.ConfirmAsync([payment.Id]);

        Assert.NotEqual(HttpStatusCode.OK, confirmed.StatusCode);
        Assert.Equal(StatusIds.CustomerPayment.AwaitingConfirmation, (await api.VietQrPaymentsAsync(reference)).Single().StatusId);
    }

    // M4 – BR-HARVEST-07 / GFR-05 with a pending sale (no time limit): the daily close check counts the kg of the sale awaiting its transfer as leftover; the transfer that then
    // arrives invoices the same kg, so they are both lost and sold.
    [Fact]
    public async Task The_kg_of_a_sale_awaiting_its_transfer_overnight_are_not_both_leftover_and_invoiced_after_the_daily_close()
    {
        using var api = new AuthApiFactory();
        var (sale, code) = await AwaitingSaleAsync(api);
        using (var scope = api.Services.CreateScope())
        {
            var check = scope.ServiceProvider.GetRequiredService<HarvestLotAutoCloseService>();
            Assert.Equal(HarvestLotAutoCloseOutcome.Closed, await check.CheckAsync(sale.LotId, DateOnly.FromDateTime(DateTime.UtcNow).AddDays(60), CancellationToken.None));
        }

        Assert.Equal("CompletedSale", (await api.TransferAsync(code, 300_000m)).Outcome());

        var (estimated, leftover, invoiced) = await api.QueryAsync(async db => (
            await db.HarvestLots.Where(l => l.Id == sale.LotId).Select(l => l.EstimatedKg).SingleAsync(),
            await db.HarvestLots.Where(l => l.Id == sale.LotId).Select(l => l.LeftoverKg ?? 0m).SingleAsync(),
            await db.SalesInvoiceLines.Where(l => l.HarvestLotId == sale.LotId).SumAsync(l => l.Kg)));
        Assert.Equal(10m, invoiced);
        Assert.True(leftover + invoiced <= estimated, $"leftover {leftover} kg + invoiced {invoiced} kg > estimated {estimated} kg");
    }

    // L – API-13 answers 5xx only when a save failed: a transfer time the business clock cannot convert (year 9999) is refused or ignored, not a 500 retried for ever.
    [Fact]
    public async Task A_transfer_time_at_the_end_of_the_calendar_does_not_make_the_notification_fail()
    {
        using var api = new AuthApiFactory();
        var (_, code) = await AwaitingSaleAsync(api);
        var secret = NewSecret();
        using var hooked = api.WithWebhookSecret(secret);
        using var bank = hooked.CreateClient();

        var response = await bank.NotifyAsync(Body(Transfer(NewReference(), 300_000m, code, "9999-12-31T23:59:59+00:00")), secret);

        Assert.True((int)response.StatusCode < 500, $"the notification answered {(int)response.StatusCode}");
    }

    /// <summary>Counts the reads of the money received without an invoice (one per sale on a list is a query per row).</summary>
    private sealed class CountingPayments(ICustomerPaymentRepository inner) : ICustomerPaymentRepository
    {
        public static int ReceivedReads;

        public void Add(CustomerPayment payment) => inner.Add(payment);

        public void RemoveAllocation(CustomerPaymentAllocation allocation) => inner.RemoveAllocation(allocation);

        public void AddAllocation(CustomerPaymentAllocation allocation) => inner.AddAllocation(allocation);

        public Task<bool> HasMoneyPaidBackAsync(Guid paymentId, CancellationToken cancellationToken) => inner.HasMoneyPaidBackAsync(paymentId, cancellationToken);

        public Task<Guid?> FindIdByCodeAsync(string code, Guid? exceptPaymentId, CancellationToken cancellationToken) => inner.FindIdByCodeAsync(code, exceptPaymentId, cancellationToken);

        public Task<IReadOnlyList<CustomerPayment>> GetPendingTransfersAsync(CancellationToken cancellationToken) => inner.GetPendingTransfersAsync(cancellationToken);

        public Task<CustomerPayment?> GetForEditAsync(Guid paymentId, CancellationToken cancellationToken) => inner.GetForEditAsync(paymentId, cancellationToken);

        public Task<IReadOnlyList<CustomerPayment>> GetForEditAsync(IReadOnlyCollection<Guid> paymentIds, CancellationToken cancellationToken) =>
            inner.GetForEditAsync(paymentIds, cancellationToken);

        public Task<CustomerPayment?> GetAsync(Guid paymentId, CancellationToken cancellationToken) => inner.GetAsync(paymentId, cancellationToken);

        public Task<CustomerPayment?> FindByDeduplicationKeyAsync(string key, CancellationToken cancellationToken) => inner.FindByDeduplicationKeyAsync(key, cancellationToken);

        public Task<CustomerPayment?> FindNotCancelledByBankReferenceAsync(string bankReference, CancellationToken cancellationToken) =>
            inner.FindNotCancelledByBankReferenceAsync(bankReference, cancellationToken);

        public Task<CustomerPayment?> FindRecordedTransferAsync(string bankReference, CancellationToken cancellationToken) =>
            inner.FindRecordedTransferAsync(bankReference, cancellationToken);

        public Task<IReadOnlyDictionary<string, decimal>> ReceivedWithoutInvoiceAsync(IReadOnlyCollection<string> invoiceCodes, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref ReceivedReads);
            return inner.ReceivedWithoutInvoiceAsync(invoiceCodes, cancellationToken);
        }

        public Task<(IReadOnlyList<CustomerPayment> Items, int Total, decimal TotalAmount)> AwaitingConfirmationAsync(int page, int pageSize, CancellationToken cancellationToken) =>
            inner.AwaitingConfirmationAsync(page, pageSize, cancellationToken);
    }

    // L – N+1: GET pending reads the money received of each sale with its own query; the list of a zone should not cost a query per sale.
    [Fact]
    public async Task The_list_of_sales_awaiting_their_transfer_does_not_query_per_sale()
    {
        using var api = new AuthApiFactory();
        var sale = await api.SaleSetupAsync();
        await api.UseReceivingAccountAsync();
        for (var i = 0; i < 3; i++)
        {
            await sale.Manager.Client.AwaitTransferAsync(Invoice(sale.CustomerId, [Line(sale.ProductId, sale.LotId, 1m)], VietQr()));
        }

        await using var counted = api.WithWebHostBuilder(b => b.ConfigureTestServices(services =>
        {
            var real = services.Single(d => d.ServiceType == typeof(ICustomerPaymentRepository));
            services.Remove(real);
            services.AddScoped<ICustomerPaymentRepository>(provider =>
                new CountingPayments((ICustomerPaymentRepository)ActivatorUtilities.CreateInstance(provider, real.ImplementationType!)));
        }));
        using var client = counted.CreateClient();
        client.DefaultRequestHeaders.Authorization = sale.Manager.Client.DefaultRequestHeaders.Authorization;
        Interlocked.Exchange(ref CountingPayments.ReceivedReads, 0);

        var list = await client.GetFromJsonAsync<JsonElement[]>("/api/sales-invoices/pending");

        Assert.Equal(3, list!.Length);
        Assert.True(CountingPayments.ReceivedReads <= 1, $"{CountingPayments.ReceivedReads} reads of the money received for 3 sales");
    }

    // Rights: the list of sales awaiting their transfer is for sellers (SALES_INVOICES Create); the Accountant and a trader get 403, anonymous 401.
    [Fact]
    public async Task Only_sellers_list_the_sales_awaiting_their_transfer()
    {
        using var api = new AuthApiFactory();
        await AwaitingSaleAsync(api);
        var accountant = await api.AllZonesAccountantAsync();
        var trader = await api.TraderAsync();
        using var anonymous = api.CreateClient();

        Assert.Equal(HttpStatusCode.Forbidden, (await accountant.Client.GetAsync("/api/sales-invoices/pending")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await trader.Client.GetAsync("/api/sales-invoices/pending")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync("/api/sales-invoices/pending")).StatusCode);
    }
}
