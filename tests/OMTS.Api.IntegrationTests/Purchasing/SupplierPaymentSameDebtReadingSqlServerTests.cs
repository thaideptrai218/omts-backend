using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using OMTS.Api.IntegrationTests.Infrastructure;
using OMTS.Application.Common.Exceptions;
using OMTS.Application.Common.Interfaces.Persistence;
using OMTS.Application.Features.Notifications;
using OMTS.Domain.Common;
using OMTS.Domain.Constants;
using OMTS.Infrastructure.Messaging.Fakes;
using static OMTS.Api.IntegrationTests.Purchasing.PurchasingSqlServerKit;
using static OMTS.Api.IntegrationTests.Purchasing.PurchasingTestKit;
using FarmingHost = OMTS.Api.IntegrationTests.Farming.FarmingSqlServerHost;

namespace OMTS.Api.IntegrationTests.Purchasing;

/// <summary>
/// M2 of the QA report on a real SQL Server: two saves that read one debt of one supplier cannot both succeed. A barrier holds
/// each request right after its reading until the other has read too, so the overlap is certain rather than a matter of timing.
/// </summary>
public class SupplierPaymentSameDebtReadingSqlServerTests
{
    private const string Debts = "/api/supplier-debts";

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

            // Later readings (the answer to a repeated key) wait for nobody.
            if (arrival <= parties)
            {
                await _everyoneRead.Task.WaitAsync(TimeSpan.FromSeconds(60));
            }
        }
    }

    private sealed class DebtQueryWithBarrier(ISupplierDebtQuery inner, Barrier barrier) : ISupplierDebtQuery
    {
        public async Task<IReadOnlyDictionary<Guid, SupplierDebtSums>> SumsAsync(Guid? supplierId, DateOnly asOf, CancellationToken cancellationToken)
        {
            var sums = await inner.SumsAsync(supplierId, asOf, cancellationToken);
            await barrier.PassAsync();
            return sums;
        }

        public Task<IReadOnlyList<DebtSupplierRow>> SuppliersAsync(IReadOnlyCollection<Guid> supplierIds, string? search, CancellationToken cancellationToken) =>
            inner.SuppliersAsync(supplierIds, search, cancellationToken);

        public Task<IReadOnlyList<DebtReceiptRow>> ReceiptsAsync(IReadOnlyCollection<Guid> supplierIds, CancellationToken cancellationToken) =>
            inner.ReceiptsAsync(supplierIds, cancellationToken);

        public Task<IReadOnlyList<DebtMoneyBackRow>> MoneyBackAsync(IReadOnlyCollection<Guid> supplierIds, CancellationToken cancellationToken) =>
            inner.MoneyBackAsync(supplierIds, cancellationToken);

        public Task<(IReadOnlyList<DebtReceiptRow> Receipts, IReadOnlyList<StatementReturnRow> Returns, IReadOnlyList<StatementPaymentRow> Payments)> StatementRowsAsync(
            Guid supplierId, DateOnly from, DateOnly to, CancellationToken cancellationToken) =>
            inner.StatementRowsAsync(supplierId, from, to, cancellationToken);
    }

    /// <summary>A save that fails on the key fails on the supplier row instead: which comes first is up to the order EF writes in.</summary>
    private sealed class ConflictInsteadOfDuplicate(IUnitOfWork inner) : IUnitOfWork
    {
        public async Task<int> SaveChangesAsync(CancellationToken cancellationToken = default)
        {
            try
            {
                return await inner.SaveChangesAsync(cancellationToken);
            }
            catch (DuplicateRecordException exception)
            {
                throw new ConcurrencyConflictException(exception);
            }
        }

        public Task ReloadAsync(object entity, CancellationToken cancellationToken = default) => inner.ReloadAsync(entity, cancellationToken);

        public void ExpectRowVersion(AuditableEntity entity, byte[] rowVersion) => inner.ExpectRowVersion(entity, rowVersion);

        public void MarkChanged(AuditableEntity entity) => inner.MarkChanged(entity);
    }

    /// <summary>The API on the host's database, where the first two requests that read a debt read it together.</summary>
    private static WebApplicationFactory<Program> WithTwoReadingsTogether(FarmingHost host, Action<IServiceCollection>? also = null)
    {
        var barrier = new Barrier(2);
        return host.Api.WithWebHostBuilder(b => b.ConfigureTestServices(services =>
        {
            var real = services.Single(d => d.ServiceType == typeof(ISupplierDebtQuery));
            services.Remove(real);
            services.AddScoped<ISupplierDebtQuery>(provider =>
                new DebtQueryWithBarrier((ISupplierDebtQuery)ActivatorUtilities.CreateInstance(provider, real.ImplementationType!), barrier));
            also?.Invoke(services);
        }));
    }

    private static void ConflictOnTheKey(IServiceCollection services)
    {
        var real = services.Single(d => d.ServiceType == typeof(IUnitOfWork));
        services.Remove(real);
        services.AddScoped<IUnitOfWork>(provider => new ConflictInsteadOfDuplicate((IUnitOfWork)ActivatorUtilities.CreateInstance(provider, real.ImplementationType!)));
    }

    private static HttpClient ClientOf(WebApplicationFactory<Program> api, HttpClient loggedIn)
    {
        var client = api.CreateClient();
        client.DefaultRequestHeaders.Authorization = loggedIn.DefaultRequestHeaders.Authorization;
        return client;
    }

    /// <summary>One answer is the success, the other MSG06.</summary>
    private static async Task<(JsonElement Winner, int WinnerIndex)> OneWinsAsync(HttpResponseMessage[] responses, HttpStatusCode success)
    {
        var codes = new[] { await responses[0].CodeOrStatusAsync(), await responses[1].CodeOrStatusAsync() };
        var winner = Array.FindIndex(responses, r => r.StatusCode == success);
        Assert.True(winner >= 0 && responses[1 - winner].StatusCode == HttpStatusCode.Conflict, string.Join(", ", codes));
        Assert.Equal("409 MSG06", codes[1 - winner]);
        return (await responses[winner].ReadAsync(success), winner);
    }

    [SqlServerFact]
    public async Task Of_two_payments_that_read_one_debt_one_succeeds_with_the_true_debt_and_the_other_answers_MSG06_and_saves_nothing()
    {
        await using var host = await FarmingHost.StartAsync();
        var world = await host.WorldAsync();
        await world.CompletedAsync(new LineBody(world.Other, 1, 1_000_000));
        await using var api = WithTwoReadingsTogether(host);
        var client = ClientOf(api, world.Owner);
        var body = new PaymentBody { SupplierId = world.SupplierId, Amount = 100_000 };

        var (winner, _) = await OneWinsAsync(await InParallelAsync(2, _ => client.PostPaymentAsync(body)), HttpStatusCode.Created);

        Assert.Equal(900_000m, winner.Money("debtAfter"));
        Assert.Equal(900_000m, await world.DebtAsync());
        Assert.Equal(1, await host.QueryAsync(db => db.SupplierPayments.CountAsync(p => p.SupplierId == world.SupplierId)));
        var told = await host.QueryAsync(db => db.Notifications
            .Where(n => n.Type == NotificationTypes.SupplierTransaction && n.RecipientAccountId == host.Owner.Account.Id).Select(n => n.MessageParams).ToListAsync());
        Assert.Equal(900_000m, JsonDocument.Parse(Assert.Single(told)!).RootElement.GetProperty("debt_amount").GetDecimal());

        // The loser sends it again and reads the debt the winner left.
        var again = await (await client.PostPaymentAsync(body)).ReadAsync(HttpStatusCode.Created);
        Assert.Equal(800_000m, again.Money("debtAfter"));
    }

    // Each is at most the debt it read; together they pass it. The second now hears MSG-PURCHASE-05 on the debt that is left.
    [SqlServerFact]
    public async Task Parallel_payments_cannot_pass_the_debt_without_MSG_PURCHASE_05()
    {
        await using var host = await FarmingHost.StartAsync();
        var world = await host.WorldAsync();
        await world.CompletedAsync(new LineBody(world.Other, 1, 150_000));
        await using var api = WithTwoReadingsTogether(host);
        var client = ClientOf(api, world.Owner);
        var body = new PaymentBody { SupplierId = world.SupplierId, Amount = 100_000 };

        var (winner, _) = await OneWinsAsync(await InParallelAsync(2, _ => client.PostPaymentAsync(body)), HttpStatusCode.Created);

        Assert.Equal(50_000m, winner.Money("debtAfter"));
        Assert.Equal(50_000m, await world.DebtAsync());
        var asked = await (await client.PostPaymentAsync(body)).ProblemAsync(HttpStatusCode.Conflict, "MSG-PURCHASE-05");
        Assert.Equal((50_000m, 50_000m), (asked.Money("debtAmount"), asked.Money("advanceAmount")));
    }

    [SqlServerFact]
    public async Task Parallel_payments_of_one_supplier_cannot_share_a_bank_reference_without_MSG_PURCHASE_06()
    {
        await using var host = await FarmingHost.StartAsync();
        var world = await host.WorldAsync();
        await world.CompletedAsync(new LineBody(world.Other, 1, 1_000_000));
        await using var api = WithTwoReadingsTogether(host);
        var client = ClientOf(api, world.Owner);
        var reference = $"FT-{Guid.NewGuid():N}";
        var body = new PaymentBody { SupplierId = world.SupplierId, Amount = 100_000, Method = "BankTransfer", BankReference = reference };

        var (winner, _) = await OneWinsAsync(await InParallelAsync(2, _ => client.PostPaymentAsync(body)), HttpStatusCode.Created);

        Assert.Equal(1, await host.QueryAsync(db => db.SupplierPayments.CountAsync(p => p.BankReference == reference)));
        var asked = await (await client.PostPaymentAsync(body)).ProblemAsync(HttpStatusCode.Conflict, "MSG-PURCHASE-06");
        Assert.Equal(winner.GetProperty("code").GetString(), asked.GetProperty("paymentCode").GetString());
    }

    [SqlServerFact]
    public async Task Of_two_cancels_that_read_one_debt_one_succeeds_with_the_true_debt_and_the_other_answers_MSG06_and_keeps_its_payment()
    {
        await using var host = await FarmingHost.StartAsync();
        var world = await host.WorldAsync();
        await world.CompletedAsync(new LineBody(world.Other, 1, 1_000_000));
        var payments = new[] { await world.PayAsync(100_000), await world.PayAsync(200_000) };
        await using var api = WithTwoReadingsTogether(host);
        var client = ClientOf(api, world.Owner);

        var (winner, index) = await OneWinsAsync(await InParallelAsync(2, i => client.CancelPaymentAsync(payments[i].Id(), "Mistake")), HttpStatusCode.OK);

        var (cancelled, kept) = (payments[index], payments[1 - index]);
        Assert.Equal(700_000m + cancelled.Money("amount"), winner.Money("debtAfter"));
        Assert.Equal(winner.Money("debtAfter"), await world.DebtAsync());
        Assert.Equal(StatusIds.SupplierPayment.Recorded, await host.QueryAsync(db => db.SupplierPayments.Where(p => p.Id == kept.Id()).Select(p => p.StatusId).SingleAsync()));

        var again = await (await client.CancelPaymentAsync(kept.Id(), "Mistake")).ReadAsync(HttpStatusCode.OK);
        Assert.Equal(1_000_000m, again.Money("debtAfter"));
    }

    // The loser fails on the key or on the supplier row, whichever the save reaches first: both answer the first payment and create nothing.
    [SqlServerFact]
    public Task Two_requests_with_one_key_that_read_one_debt_both_answer_the_one_payment_and_the_owner_hears_of_it_once() => OneKeyAsync(null);

    [SqlServerFact]
    public Task The_same_when_the_loser_fails_on_the_supplier_row_rather_than_on_the_key() => OneKeyAsync(ConflictOnTheKey);

    private static async Task OneKeyAsync(Action<IServiceCollection>? also)
    {
        await using var host = await FarmingHost.StartAsync();
        var world = await host.WorldAsync();
        await world.CompletedAsync(new LineBody(world.Other, 1, 1_000_000));
        await using var api = WithTwoReadingsTogether(host, also);
        var client = ClientOf(api, world.Owner);
        var key = $"retry-{Guid.NewGuid():N}";

        var responses = await InParallelAsync(2, _ => client.PostPaymentAsync(new PaymentBody { SupplierId = world.SupplierId, Amount = 100_000, DeduplicationKey = key }));

        var (first, second) = (await responses[0].ReadAsync(HttpStatusCode.Created), await responses[1].ReadAsync(HttpStatusCode.Created));
        Assert.Equal(first.Id(), second.Id());
        Assert.Equal((900_000m, 900_000m), (first.Money("debtAfter"), second.Money("debtAfter")));
        Assert.Equal(1, await host.QueryAsync(db => db.SupplierPayments.CountAsync(p => p.SupplierId == world.SupplierId)));
        Assert.Equal(1, await host.QueryAsync(db => db.Notifications.CountAsync(n => n.RelatedRecordId == first.Id() && n.RecipientAccountId == host.Owner.Account.Id)));
        var pushes = api.Services.GetRequiredService<FakePushSender>().Inbox.List(host.Owner.Account.Id.ToString());
        Assert.Single(pushes, p => p.MessageParams is { } values && values.TryGetProperty("payment_code", out var code) && code.GetString() == first.GetProperty("code").GetString());
    }

    // A loser to another payment holds no payment of its key: MSG06, and the same key then records it, once.
    [SqlServerFact]
    public async Task A_key_that_lost_to_another_payment_stays_free_for_the_retry()
    {
        await using var host = await FarmingHost.StartAsync();
        var world = await host.WorldAsync();
        await world.CompletedAsync(new LineBody(world.Other, 1, 1_000_000));
        await using var api = WithTwoReadingsTogether(host);
        var client = ClientOf(api, world.Owner);
        var key = $"retry-{Guid.NewGuid():N}";
        var keyed = new PaymentBody { SupplierId = world.SupplierId, Amount = 100_000, DeduplicationKey = key };

        var (_, winner) = await OneWinsAsync(await InParallelAsync(2, i => client.PostPaymentAsync(i == 0 ? keyed : keyed with { DeduplicationKey = null, Amount = 7 })), HttpStatusCode.Created);

        var retried = await (await client.PostPaymentAsync(keyed)).ReadAsync(HttpStatusCode.Created);
        Assert.Equal(winner == 0 ? 1_000_000m - 100_000m : 1_000_000m - 7m - 100_000m, await world.DebtAsync());
        Assert.Equal(1, await host.QueryAsync(db => db.SupplierPayments.CountAsync(p => p.DeduplicationKey == key)));
        Assert.Equal(key, await host.QueryAsync(db => db.SupplierPayments.Where(p => p.Id == retried.Id()).Select(p => p.DeduplicationKey).SingleAsync()));
    }

    // The price of holding the row: a payment or its cancel makes an open edit form of the supplier stale (client list §10).
    [SqlServerFact]
    public async Task A_payment_and_its_cancel_change_the_row_version_of_the_supplier_so_an_edit_form_opened_before_answers_MSG06()
    {
        await using var host = await FarmingHost.StartAsync();
        var world = await host.WorldAsync();
        var url = $"/api/suppliers/{world.SupplierId}";
        var opened = await (await world.Owner.GetAsync(url)).ReadAsync(HttpStatusCode.OK);
        object Edit(JsonElement form) => new { name = "Edited", supplierType = "Seed", phoneNumber = form.GetProperty("phoneNumber").GetString(), rowVersion = form.GetProperty("rowVersion").GetString() };

        var payment = await world.PayAsync(1_000);

        await (await world.Owner.PutAsJsonAsync(url, Edit(opened))).ProblemAsync(HttpStatusCode.Conflict, "MSG06");
        var afterPayment = await (await world.Owner.GetAsync(url)).ReadAsync(HttpStatusCode.OK);
        Assert.NotEqual(opened.GetProperty("rowVersion").GetString(), afterPayment.GetProperty("rowVersion").GetString());

        await (await world.Owner.CancelPaymentAsync(payment.Id(), "Mistake")).ReadAsync(HttpStatusCode.OK);

        await (await world.Owner.PutAsJsonAsync(url, Edit(afterPayment))).ProblemAsync(HttpStatusCode.Conflict, "MSG06");
        var current = await (await world.Owner.GetAsync(url)).ReadAsync(HttpStatusCode.OK);
        await (await world.Owner.PutAsJsonAsync(url, Edit(current))).ReadAsync(HttpStatusCode.OK);
    }
}
