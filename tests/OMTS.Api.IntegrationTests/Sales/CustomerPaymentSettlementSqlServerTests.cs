using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using OMTS.Api.IntegrationTests.Farming;
using OMTS.Api.IntegrationTests.Infrastructure;
using OMTS.Application.Features.Notifications;
using OMTS.Domain.Constants;
using OMTS.Domain.Entities.Identity;
using OMTS.Domain.Entities.Sales;
using OMTS.Domain.Enums;
using static OMTS.Api.IntegrationTests.Sales.CustomerPaymentSettlementTestKit;
using static OMTS.Api.IntegrationTests.Sales.CustomerPaymentSqlServerTests;
using static OMTS.Api.IntegrationTests.Sales.CustomerPaymentTestKit;
using SqlKit = OMTS.Api.IntegrationTests.Purchasing.PurchasingSqlServerKit;

namespace OMTS.Api.IntegrationTests.Sales;

/// <summary>
/// DEBT-02.6 races on a real SQL Server (the held reads of <see cref="CustomerPaymentSqlServerTests"/>: each request is held right after it read its payments until the other has read
/// too): two settlements of one payment, a settlement against a cancellation, against the replay of the bank notification, two payments attached to one profile at once, and the
/// unique index on the money paid back.
/// </summary>
public class CustomerPaymentSettlementSqlServerTests
{
    /// <summary>A reported transfer whose code matches no saved invoice: a payment without a customer, marked (DEBT-02.6). Returns its id and bank reference.</summary>
    private static async Task<(Guid Id, string Reference)> OrphanAsync(World w, decimal amount)
    {
        await w.Host.UseDbAsync(db => db.Cooperatives.Single(c => c.Id == w.Host.CooperativeId).BankAccount = new ReceivingBankAccount
        {
            BankBin = VietQrSaleTestKit.BankBin, BankName = "Vietcombank", AccountNumber = VietQrSaleTestKit.AccountNumber, AccountHolderName = "HTX DEMO", AcceptedAt = DateTimeOffset.UtcNow,
        });
        var reference = VietQrSaleTestKit.NewReference();
        Assert.Equal("WithoutCustomer", await TransferAsync(w, reference, amount));
        return (await w.Host.QueryAsync(db => db.CustomerPayments.Where(p => p.BankReference == reference).Select(p => p.Id).SingleAsync()), reference);
    }

    private static async Task<string> TransferAsync(World w, string reference, decimal amount)
    {
        using var bank = w.Host.Api.CreateClient();
        var response = await bank.PostAsJsonAsync("/api/dev/payment-notifications", new { invoiceCode = NewUnknownCode(), amount, bankReference = reference });
        return Assert.Single((await response.ReadAsync(HttpStatusCode.OK)).GetProperty("transactions").EnumerateArray()).Outcome();
    }

    private static Task<int> PaidBackCountAsync(World w, Guid paymentId) =>
        w.Host.QueryAsync(db => db.CustomerPayments.CountAsync(p => p.PaidBackForPaymentId == paymentId));

    // DEBT-02.6 settle twice at once: both read the payment Awaiting; only one save passes the row version or the unique index, so money is paid back once.
    [SqlServerFact]
    public async Task Of_two_settlements_of_one_payment_at_once_one_pays_back_once_and_the_other_hears_MSG_GEN_09()
    {
        await using var host = await FarmingSqlServerHost.StartAsync();
        var w = await WorldAsync(host);
        var (orphan, _) = await OrphanAsync(w, 120_000m);
        var (api, accountant) = Held(w);
        await using var __ = api;

        await OneWinsAsync(await SqlKit.InParallelAsync(2, _ => accountant.SettleAsync(PayBack(orphan))));

        Assert.Equal(StatusIds.CustomerPayment.Completed, await StatusOfAsync(w, orphan));
        Assert.Equal(1, await PaidBackCountAsync(w, orphan));
        Assert.Equal(1, await host.QueryAsync(db => db.AuditEvents.CountAsync(e => e.RecordId == orphan && e.Action == "CustomerPayment.Settled")));
        Assert.Equal(1, await NoticesAsync(w));
    }

    // Two attachments of one payment to two profiles at once: the payment lands on one profile only, the other hears MSG-GEN-09, and one audit row and one notice are written.
    [SqlServerFact]
    public async Task Of_two_attachments_of_one_payment_at_once_one_profile_gets_it_and_the_other_hears_MSG_GEN_09()
    {
        await using var host = await FarmingSqlServerHost.StartAsync();
        var w = await WorldAsync(host);
        var otherCustomer = (await w.Owner.CreateCustomerAsync()).Id();
        var customers = new[] { w.CustomerId, otherCustomer };
        var (orphan, _) = await OrphanAsync(w, 120_000m);
        var (api, accountant) = Held(w);
        await using var __ = api;

        var winner = await OneWinsAsync(await SqlKit.InParallelAsync(2, i => accountant.SettleAsync(ToCustomer(orphan, customers[i]))));

        var row = await host.QueryAsync(db => db.CustomerPayments.Where(p => p.Id == orphan).SingleAsync());
        Assert.Equal((StatusIds.CustomerPayment.Completed, customers[winner]), (row.StatusId, row.CustomerId!.Value));
        Assert.Equal(-120_000m, await w.Owner.CustomerDebtAsync(customers[winner]));
        Assert.Equal(0m, await w.Owner.CustomerDebtAsync(customers[1 - winner]));
        Assert.Equal(1, await host.QueryAsync(db => db.AuditEvents.CountAsync(e => e.RecordId == orphan && e.Action == "CustomerPayment.Settled")));
        Assert.Equal(1, await NoticesAsync(w));
    }

    // Both read the debt of one profile; the loser on the profile reads it again, so each answer and notice tells the debt its save left.
    [SqlServerFact]
    public async Task Two_payments_attached_to_one_profile_at_once_both_succeed_and_each_tells_the_true_debt_after_it()
    {
        await using var host = await FarmingSqlServerHost.StartAsync();
        var w = await WorldAsync(host);
        var (first, _) = await OrphanAsync(w, 120_000m);
        var (second, _) = await OrphanAsync(w, 80_000m);
        var ids = new[] { first, second };
        var (api, accountant) = Held(w);
        await using var __ = api;

        var answers = await SqlKit.InParallelAsync(2, i => accountant.SettleAsync(ToCustomer(ids[i], w.CustomerId)));

        var after = new List<decimal>();
        foreach (var answer in answers)
        {
            after.Add((await answer.ReadAsync(HttpStatusCode.OK)).Settled().Money("debtAfter"));
        }

        Assert.Equal(-200_000m, await w.Owner.CustomerDebtAsync(w.CustomerId));
        Assert.Contains(-200_000m, after);
        Assert.Contains(after.Single(d => d != -200_000m), new[] { -120_000m, -80_000m });
        Assert.Equal(2, await NoticesAsync(w));
        var told = await host.QueryAsync(db => db.Notifications
            .Where(n => n.Type == NotificationTypes.CustomerPaymentTransaction && n.RecipientAccountId == host.Owner.Account.Id).Select(n => n.MessageParams).ToListAsync());
        Assert.Equal(
            after.Order(),
            told.Select(p => JsonDocument.Parse(p!).RootElement.GetProperty("debts").EnumerateArray().Single().GetProperty("debt_amount").GetDecimal()).Order());
    }

    // Settle (pay back) against cancel: exactly one is applied. Never a payment cancelled with its money paid back standing, or a payment settled twice.
    [SqlServerFact]
    public async Task A_settlement_by_money_paid_back_racing_a_cancellation_applies_only_one_of_them()
    {
        await using var host = await FarmingSqlServerHost.StartAsync();
        var w = await WorldAsync(host);
        var (orphan, _) = await OrphanAsync(w, 120_000m);
        var (api, accountant) = Held(w);
        await using var __ = api;

        var winner = await OneWinsAsync(await SqlKit.InParallelAsync(2, i => i == 0
            ? accountant.SettleAsync(PayBack(orphan))
            : accountant.CancelAsync(orphan, "Reported by mistake")));

        var (status, paidBack) = (await StatusOfAsync(w, orphan), await PaidBackCountAsync(w, orphan));
        Assert.Equal(winner == 0 ? (StatusIds.CustomerPayment.Completed, 1) : (StatusIds.CustomerPayment.Cancelled, 0), (status, paidBack));
        Assert.Equal(1, await NoticesAsync(w));
    }

    // Settle against the webhook replay of the same transfer: the replay is AlreadyRecorded whatever the moment, so one row and one settlement.
    [SqlServerFact]
    public async Task A_replay_of_the_bank_notification_racing_a_settlement_records_nothing_more()
    {
        await using var host = await FarmingSqlServerHost.StartAsync();
        var w = await WorldAsync(host);
        var (orphan, reference) = await OrphanAsync(w, 120_000m);

        var settle = w.Accountant.SettleAsync(ToCustomer(orphan, w.CustomerId));
        var replay = TransferAsync(w, reference, 120_000m);
        await Task.WhenAll(settle, replay);

        Assert.Equal(HttpStatusCode.OK, (await settle).StatusCode);
        Assert.Equal("AlreadyRecorded", await replay);
        Assert.Equal(1, await host.QueryAsync(db => db.CustomerPayments.CountAsync(p => p.BankReference == reference)));
        Assert.Equal(-120_000m, await w.Owner.CustomerDebtAsync(w.CustomerId));
    }

    // The unique index is the last guard: two money paid back rows that are not cancelled cannot exist for one payment; a cancelled one no longer counts.
    [SqlServerFact]
    public async Task The_database_refuses_a_second_money_paid_back_of_one_payment_while_the_first_stands()
    {
        await using var host = await FarmingSqlServerHost.StartAsync();
        var w = await WorldAsync(host);
        var (orphan, _) = await OrphanAsync(w, 120_000m);
        await w.Accountant.SettleAsync(PayBack(orphan));

        CustomerPayment Second() => new()
        {
            Code = "PT" + AcsTestKit.NewCode(), Kind = CustomerPaymentKind.PaidBack, PaymentDate = PurchaseRequestTestKit.Today, Amount = 1m, Method = CustomerPaymentMethod.Cash,
            StatusId = StatusIds.CustomerPayment.Completed, PaidBackForPaymentId = orphan,
        };

        await Assert.ThrowsAnyAsync<Exception>(() => host.UseDbAsync(db => db.CustomerPayments.Add(Second())));

        await host.UseDbAsync(db => db.CustomerPayments.Where(p => p.PaidBackForPaymentId == orphan).ExecuteUpdate(s => s.SetProperty(p => p.StatusId, StatusIds.CustomerPayment.Cancelled)));
        await host.UseDbAsync(db => db.CustomerPayments.Add(Second()));
    }
}
