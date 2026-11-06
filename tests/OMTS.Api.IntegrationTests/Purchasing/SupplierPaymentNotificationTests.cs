using System.Net;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using OMTS.Api.IntegrationTests.Infrastructure;
using OMTS.Api.IntegrationTests.Notifications;
using OMTS.Application.Features.Notifications;
using OMTS.Domain.Entities.Identity;
using OMTS.Domain.Enums;
using OMTS.Infrastructure.Messaging.Fakes;
using static OMTS.Api.IntegrationTests.Infrastructure.AcsTestKit;

namespace OMTS.Api.IntegrationTests.Purchasing;

/// <summary>BR-PURCHASE-05 / MSG-PURCHASE-04: every transaction goes to every Owner, also the Owner who did it, and is always pushed.</summary>
public class SupplierPaymentNotificationTests(AuthApiFactory factory) : IClassFixture<AuthApiFactory>
{
    [Fact]
    public async Task The_owner_is_told_of_a_payment_money_paid_back_and_a_cancellation_with_the_debt_after_it()
    {
        var world = await factory.WorldAsync();
        var name = await factory.QueryAsync(db => db.Suppliers.Where(s => s.Id == world.SupplierId).Select(s => s.Name).SingleAsync());
        await world.CompletedAsync(new LineBody(world.Other, 1, 100_000));
        var accountant = await factory.AccountantAsync();

        var payment = await (await accountant.PostPaymentAsync(new PaymentBody { SupplierId = world.SupplierId, Amount = 40_000 })).ReadAsync(HttpStatusCode.Created);
        var paidBack = await (await accountant.PostPaymentAsync(new PaymentBody { SupplierId = world.SupplierId, Kind = "PaidBack", Amount = 5_000 })).ReadAsync(HttpStatusCode.Created);
        var (paymentCode, paidBackCode) = (payment.GetProperty("code").GetString(), paidBack.GetProperty("code").GetString());
        await (await accountant.CancelPaymentAsync(paidBack.Id(), "Not received")).ReadAsync(HttpStatusCode.OK);
        var actor = payment.GetProperty("recordedBy").GetProperty("name").GetString();

        var bodies = await factory.QueryAsync(db => db.Notifications
            .Where(n => n.Type == NotificationTypes.SupplierTransaction && (n.RelatedRecordId == payment.Id() || n.RelatedRecordId == paidBack.Id()))
            .Select(n => new { n.Body, n.MessageCode, n.IsMandatory, n.RelatedRecordType, n.RelatedRecordCode, n.RelatedRecordId, n.RecipientAccountId, n.MessageParams })
            .ToListAsync());
        var distinct = bodies.Select(n => n.Body).Distinct().ToList();
        Assert.Equal(
            new[]
            {
                $"{actor} recorded payment {paymentCode} of 40,000 VND to {name}. The cooperative now owes {name} 60,000 VND.",
                $"{actor} recorded money paid back {paidBackCode} of 5,000 VND by {name}. The cooperative now owes {name} 65,000 VND.",
                $"{actor} recorded the cancellation of money paid back {paidBackCode} of 5,000 VND by {name}. The cooperative now owes {name} 60,000 VND.",
            }.Order(StringComparer.Ordinal),
            distinct.Order(StringComparer.Ordinal));
        Assert.All(bodies, n => Assert.Equal(("MSG-PURCHASE-04", true, RelatedRecordTypes.SupplierPayment), (n.MessageCode, n.IsMandatory, n.RelatedRecordType)));
        var parameters = JsonDocument.Parse(bodies.First(n => n.RelatedRecordId == payment.Id()).MessageParams!).RootElement;
        Assert.Equal(("Payment", paymentCode, 40_000m, 60_000m), (parameters.GetProperty("transaction_kind").GetString(), parameters.GetProperty("payment_code").GetString(), parameters.GetProperty("amount").GetDecimal(), parameters.GetProperty("debt_amount").GetDecimal()));

        // MSG-PURCHASE-04 is to the Owner only: the Accountant who did it gets nothing.
        Assert.DoesNotContain((await accountant.NotificationPageAsync()).Items(), n => n.GetProperty("type").GetString() == NotificationTypes.SupplierTransaction);
    }

    [Fact]
    public async Task A_debt_below_zero_is_said_as_paid_in_advance()
    {
        var world = await factory.WorldAsync();
        var name = await factory.QueryAsync(db => db.Suppliers.Where(s => s.Id == world.SupplierId).Select(s => s.Name).SingleAsync());
        var payment = await world.PayAsync(1_500);

        var body = await factory.QueryAsync(db => db.Notifications.Where(n => n.RelatedRecordId == payment.Id()).Select(n => n.Body).FirstAsync());
        Assert.EndsWith($"The cooperative has now paid {name} 1,500 VND in advance.", body);
    }

    // BR-PURCHASE-05, PM 03/11: also when the Owner acts. BR-ACCOUNT-11: push cannot be turned off for this type, and it opens for the Owner.
    [Fact]
    public async Task The_owner_who_acts_is_told_and_pushed_even_with_the_push_turned_off_and_the_notification_opens_the_payment()
    {
        var password = NewPassword();
        await factory.EnsureCooperativeAsync();
        var owner = await factory.AddAccountAsync(NewCode(), password, SubjectType.CooperativeOwner);
        await factory.UpdateAsync<Account>(owner.Id, a => a.NotificationPreferences.Add(new NotificationPreference { Type = NotificationTypes.SupplierTransaction, Push = false }));
        var client = await factory.LoginAsync(owner.Username, password);
        var world = await factory.WorldAsync(client);

        var payment = await world.PayAsync(25_000);

        var row = await factory.QueryAsync(db => db.Notifications.SingleAsync(n => n.RecipientAccountId == owner.Id && n.RelatedRecordId == payment.Id()));
        Assert.True(row.IsMandatory);
        var push = factory.Services.GetRequiredService<FakePushSender>().Inbox.List(owner.Id.ToString());
        Assert.Single(push, p => p.MessageCode == "MSG-PURCHASE-04" && p.MessageParams!.Value.GetProperty("payment_code").GetString() == payment.GetProperty("code").GetString());
        var opened = await (await client.OpenNotificationAsync(row.Id)).ReadAsync(HttpStatusCode.OK);
        Assert.Equal((RelatedRecordTypes.SupplierPayment, payment.Id()), (opened.GetProperty("recordType").GetString(), opened.GetProperty("recordId").GetGuid()));
    }

    [Fact]
    public async Task A_refused_payment_sends_nothing()
    {
        var world = await factory.WorldAsync();
        var before = await factory.QueryAsync(db => db.Notifications.CountAsync(n => n.Type == NotificationTypes.SupplierTransaction));

        await (await world.Owner.PostPaymentAsync(new PaymentBody { SupplierId = world.SupplierId, Amount = 1_000 })).ProblemAsync(HttpStatusCode.Conflict, "MSG-PURCHASE-05");

        Assert.Equal(before, await factory.QueryAsync(db => db.Notifications.CountAsync(n => n.Type == NotificationTypes.SupplierTransaction)));
    }
}
