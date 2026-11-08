using System.Net;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using OMTS.Api.IntegrationTests.Cultivation;
using OMTS.Api.IntegrationTests.Farming;
using OMTS.Api.IntegrationTests.Harvest;
using OMTS.Api.IntegrationTests.Infrastructure;
using OMTS.Application.Common.Interfaces.Persistence;
using OMTS.Application.Features.Notifications;
using OMTS.Domain.Constants;
using OMTS.Domain.Entities.Sales;
using OMTS.Domain.Enums;
using static OMTS.Api.IntegrationTests.Farming.FarmingTestKit;
using static OMTS.Api.IntegrationTests.Sales.CustomerPaymentTestKit;
using static OMTS.Api.IntegrationTests.Sales.SalesInvoiceTestKit;
using SqlKit = OMTS.Api.IntegrationTests.Purchasing.PurchasingSqlServerKit;

namespace OMTS.Api.IntegrationTests.Sales;

/// <summary>
/// DEBT-02 races on a real SQL Server: a barrier holds each request right after it read its payments (or passed its checks) until the other has read too, so the two saves
/// overlap for certain. Two confirmations of one payment, a confirmation against a cancellation, two cancellations that read one debt, and one deduplication key twice.
/// </summary>
public class CustomerPaymentSqlServerTests
{
    private sealed class Barrier(int parties)
    {
        private readonly TaskCompletionSource _everyoneRead = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _arrived;

        public async Task PassAsync()
        {
            var arrival = Interlocked.Increment(ref _arrived);
            if (arrival >= parties)
            {
                _everyoneRead.TrySetResult();
            }

            // A second reading (the retry after a lost save) waits for nobody.
            if (arrival <= parties)
            {
                await _everyoneRead.Task.WaitAsync(TimeSpan.FromSeconds(60));
            }
        }
    }

    private sealed class HeldPayments(ICustomerPaymentRepository inner, Barrier barrier) : ICustomerPaymentRepository
    {
        public void Add(CustomerPayment payment) => inner.Add(payment);

        public async Task<CustomerPayment?> GetForEditAsync(Guid paymentId, CancellationToken cancellationToken) =>
            await HoldAsync(await inner.GetForEditAsync(paymentId, cancellationToken));

        public async Task<IReadOnlyList<CustomerPayment>> GetForEditAsync(IReadOnlyCollection<Guid> paymentIds, CancellationToken cancellationToken) =>
            await HoldAsync(await inner.GetForEditAsync(paymentIds, cancellationToken));

        public Task<CustomerPayment?> GetAsync(Guid paymentId, CancellationToken cancellationToken) => inner.GetAsync(paymentId, cancellationToken);

        public Task<CustomerPayment?> FindByDeduplicationKeyAsync(string key, CancellationToken cancellationToken) => inner.FindByDeduplicationKeyAsync(key, cancellationToken);

        // The last check before a payment is saved: both requests are past the key lookup.
        public async Task<CustomerPayment?> FindNotCancelledByBankReferenceAsync(string bankReference, CancellationToken cancellationToken) =>
            await HoldAsync(await inner.FindNotCancelledByBankReferenceAsync(bankReference, cancellationToken));

        public Task<(IReadOnlyList<CustomerPayment> Items, int Total, decimal TotalAmount)> AwaitingConfirmationAsync(int page, int pageSize, CancellationToken cancellationToken) =>
            inner.AwaitingConfirmationAsync(page, pageSize, cancellationToken);

        public Task<CustomerPayment?> FindVietQrByBankReferenceAsync(string bankReference, CancellationToken cancellationToken) =>
            inner.FindVietQrByBankReferenceAsync(bankReference, cancellationToken);

        public Task<decimal> ReceivedWithoutInvoiceAsync(string invoiceCode, CancellationToken cancellationToken) =>
            inner.ReceivedWithoutInvoiceAsync(invoiceCode, cancellationToken);

        private async Task<T> HoldAsync<T>(T value)
        {
            await barrier.PassAsync();
            return value;
        }
    }

    private sealed record World(FarmingSqlServerHost Host, HttpClient Owner, HttpClient Manager, HttpClient Accountant, Guid ProductId, Guid LotId, Guid CustomerId);

    private static async Task<World> WorldAsync(FarmingSqlServerHost host)
    {
        var owner = host.Owner.Client;
        var zoneId = (await owner.CreateZoneAsync()).Id();
        var raftId = (await owner.CreateRaftAsync(zoneId)).Id();
        var cycleId = (await owner.StartCycleAsync(raftId, new CycleBody(Today.AddDays(-10)))).Id();
        await owner.SeedingRecordAsync(cycleId, (await owner.SeedLotAsync()).Session([1, 2, 3, 4, 5]));
        var lotId = (await owner.HarvestLotAsync(raftId, HarvestFields.OfLanes(1))).Id();
        var productId = (await owner.CreateProductAsync(unitPrice: ListPrice)).Id();
        var customerId = (await owner.CreateCustomerAsync()).Id();
        return new World(host, owner, (await host.StaffInZoneAsync(zoneId)).Client, await SqlKit.StaffAsync(host, null, StaffRole.Accountant), productId, lotId, customerId);
    }

    /// <summary>A Pay later invoice of 30,000 VND/kg with cash at the port; the id of that cash, Awaiting confirmation.</summary>
    private static async Task<Guid> PortCashAsync(World w, decimal kg, decimal cash)
    {
        var invoice = (await w.Manager.SellAsync(Invoice(w.CustomerId, [Line(w.ProductId, w.LotId, kg)], Cash(cash, "PayLater", payLaterOwnerAgreed: true), w.LotId))).GetProperty("invoice");
        return await w.Host.QueryAsync(db => db.CustomerPayments.Where(p => p.InvoiceId == invoice.Id()).Select(p => p.Id).SingleAsync());
    }

    private static (WebApplicationFactory<Program> Api, HttpClient Accountant) Held(World w)
    {
        var barrier = new Barrier(2);
        var api = w.Host.Api.WithWebHostBuilder(b => b.ConfigureTestServices(services =>
        {
            var real = services.Single(d => d.ServiceType == typeof(ICustomerPaymentRepository));
            services.Remove(real);
            services.AddScoped<ICustomerPaymentRepository>(provider =>
                new HeldPayments((ICustomerPaymentRepository)ActivatorUtilities.CreateInstance(provider, real.ImplementationType!), barrier));
        }));
        var client = api.CreateClient();
        client.DefaultRequestHeaders.Authorization = w.Accountant.DefaultRequestHeaders.Authorization;
        return (api, client);
    }

    private static Task<int> NoticesAsync(World w) =>
        w.Host.QueryAsync(db => db.Notifications.CountAsync(n => n.Type == NotificationTypes.CustomerPaymentTransaction && n.RecipientAccountId == w.Host.Owner.Account.Id));

    private static Task<int> StatusOfAsync(World w, Guid paymentId) =>
        w.Host.QueryAsync(db => db.CustomerPayments.Where(p => p.Id == paymentId).Select(p => p.StatusId).SingleAsync());

    /// <summary>One answer is the success, the other 409 MSG-GEN-09 naming the payment; never a 500 or MSG06.</summary>
    private static async Task<int> OneWinsAsync(HttpResponseMessage[] answers)
    {
        var codes = new[] { await SqlKit.CodeOrStatusAsync(answers[0]), await SqlKit.CodeOrStatusAsync(answers[1]) };
        var winner = Array.FindIndex(answers, a => a.StatusCode == HttpStatusCode.OK);
        Assert.True(winner >= 0, string.Join(", ", codes));
        Assert.Equal("409 MSG-GEN-09", codes[1 - winner]);
        return winner;
    }

    [SqlServerFact]
    public async Task Of_two_confirmations_of_one_payment_one_confirms_it_once_and_the_other_hears_MSG_GEN_09()
    {
        await using var host = await FarmingSqlServerHost.StartAsync();
        var w = await WorldAsync(host);
        var cash = await PortCashAsync(w, 10m, 100_000m);
        var (api, accountant) = Held(w);
        await using var _ = api;

        await OneWinsAsync(await SqlKit.InParallelAsync(2, _ => accountant.ConfirmAsync([cash])));

        Assert.Equal(StatusIds.CustomerPayment.Confirmed, await StatusOfAsync(w, cash));
        Assert.Equal(1, await host.QueryAsync(db => db.AuditEvents.CountAsync(e => e.RecordId == cash && e.Action == "CustomerPayment.Confirmed")));
        Assert.Equal(1, await NoticesAsync(w));
        Assert.Equal(200_000m, await w.Owner.CustomerDebtAsync(w.CustomerId));
    }

    // DEBT-02.4 cancels a Confirmed payment too: a cancellation that loses to the confirmation reads again and cancels the payment now Confirmed (a client that sends the
    // rowVersion it showed hears MSG06 instead); a confirmation that loses hears MSG-GEN-09. Either way the payment ends Cancelled, every 200 saved one change and one notice.
    [SqlServerFact]
    public async Task A_confirmation_and_a_cancellation_of_one_payment_at_once_end_cancelled_and_each_success_saved_one_change()
    {
        await using var host = await FarmingSqlServerHost.StartAsync();
        var w = await WorldAsync(host);
        var cash = await PortCashAsync(w, 10m, 100_000m);
        var (api, accountant) = Held(w);
        await using var _ = api;

        var answers = await SqlKit.InParallelAsync(2, i => i == 0 ? accountant.ConfirmAsync([cash]) : accountant.CancelAsync(cash, "Not handed over"));

        var codes = new[] { await SqlKit.CodeOrStatusAsync(answers[0]), await SqlKit.CodeOrStatusAsync(answers[1]) };
        Assert.Equal("200", codes[1]);
        Assert.Contains(codes[0], new[] { "200", "409 MSG-GEN-09" });
        var successes = codes.Count(c => c == "200");
        Assert.Equal(StatusIds.CustomerPayment.Cancelled, await StatusOfAsync(w, cash));
        Assert.Equal(300_000m, await w.Owner.CustomerDebtAsync(w.CustomerId));
        Assert.Equal(successes, await host.QueryAsync(db => db.AuditEvents.CountAsync(e => e.RecordId == cash && (e.Action == "CustomerPayment.Confirmed" || e.Action == "CustomerPayment.Cancelled"))));
        Assert.Equal(successes, await NoticesAsync(w));
    }

    // Both read one debt; the loser on the customer profile reads it again, so each notice and answer tells the debt its save left.
    [SqlServerFact]
    public async Task Two_cancellations_that_read_one_debt_both_succeed_and_each_tells_the_true_debt_after_it()
    {
        await using var host = await FarmingSqlServerHost.StartAsync();
        var w = await WorldAsync(host);
        var first = await PortCashAsync(w, 10m, 100_000m);
        var second = await PortCashAsync(w, 10m, 40_000m);
        var (api, accountant) = Held(w);
        await using var _ = api;
        var ids = new[] { first, second };

        var answers = await SqlKit.InParallelAsync(2, i => accountant.CancelAsync(ids[i], "Not handed over"));

        var after = new List<decimal>();
        foreach (var answer in answers)
        {
            after.Add((await answer.ReadAsync(HttpStatusCode.OK)).Money("debtAfter"));
        }

        Assert.Equal(600_000m, await w.Owner.CustomerDebtAsync(w.CustomerId));
        Assert.Contains(600_000m, after);
        Assert.Contains(after.Single(d => d != 600_000m), new[] { 600_000m - 100_000m, 600_000m - 40_000m });
        var told = await host.QueryAsync(db => db.Notifications
            .Where(n => n.Type == NotificationTypes.CustomerPaymentTransaction && n.RecipientAccountId == host.Owner.Account.Id).Select(n => n.MessageParams).ToListAsync());
        Assert.Equal(after.Order(), told.Select(p => JsonDocument.Parse(p!).RootElement.GetProperty("debt_amount").GetDecimal()).Order());
    }

    [SqlServerFact]
    public async Task Two_requests_with_one_deduplication_key_record_one_payment_and_the_owner_hears_of_it_once()
    {
        await using var host = await FarmingSqlServerHost.StartAsync();
        var w = await WorldAsync(host);
        await PortCashAsync(w, 10m, 100_000m);
        var (api, accountant) = Held(w);
        await using var _ = api;
        var key = "retry-" + AcsTestKit.NewCode();
        var body = MoneyBody(w.CustomerId, 100_000m).With("deduplicationKey", key);

        var answers = await SqlKit.InParallelAsync(2, _ => accountant.RecordAsync(body));

        var (one, two) = (await answers[0].ReadAsync(HttpStatusCode.Created), await answers[1].ReadAsync(HttpStatusCode.Created));
        Assert.Equal(one.Payment().Id(), two.Payment().Id());
        Assert.Equal((100_000m, 100_000m), (one.Money("debtAfter"), two.Money("debtAfter")));
        Assert.Equal(1, await host.QueryAsync(db => db.CustomerPayments.CountAsync(p => p.DeduplicationKey == key)));
        Assert.Equal(100_000m, await w.Owner.CustomerDebtAsync(w.CustomerId));
        Assert.Equal(1, await NoticesAsync(w));
    }
}
