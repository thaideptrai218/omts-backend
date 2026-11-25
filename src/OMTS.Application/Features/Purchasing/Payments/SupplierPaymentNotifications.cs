using Microsoft.Extensions.Options;
using OMTS.Application.Common;
using OMTS.Application.Common.Interfaces;
using OMTS.Application.Common.Interfaces.Persistence;
using OMTS.Application.Common.Messages;
using OMTS.Application.Features.Notifications;
using OMTS.Domain.Entities.Purchasing;
using OMTS.Domain.Enums;

namespace OMTS.Application.Features.Purchasing.Payments;

/// <summary>
/// BR-PURCHASE-05 for payments: MSG-PURCHASE-04 to every Owner for a payment, money paid back and a cancelled payment, mandatory,
/// also when the Owner did it. The rows join the caller's unit of work; the SMS goes out after its commit.
/// </summary>
public sealed class SupplierPaymentNotifications(
    IZoneRecipientQuery recipients,
    IAccountRepository accounts,
    INotificationDispatcher notifications,
    INotificationRepository notificationRows,
    IOptions<SupplierSmsBudgetOptions> budget,
    TimeProvider timeProvider,
    ICurrentUser currentUser)
{
    /// <param name="cancelled">The payment was just cancelled.</param>
    /// <param name="debtAfter">The supplier debt once this save is done.</param>
    public async Task TransactionAsync(SupplierPayment payment, string supplierName, bool cancelled, decimal debtAfter, CancellationToken cancellationToken)
    {
        var owners = await recipients.GetOwnersAndRolesAsync([], cancellationToken);
        var paidBack = payment.Kind == SupplierPaymentKind.PaidBack;
        var transaction = (cancelled, paidBack) switch
        {
            (false, false) => "payment",
            (false, true) => "money paid back",
            (true, false) => "the cancellation of payment",
            (true, true) => "the cancellation of money paid back",
        };
        var message = UserMessages.SupplierTransaction(
            await PurchasingActorName.OfAsync(currentUser, accounts, cancellationToken),
            transaction,
            $"{(paidBack ? "PaidBack" : "Payment")}{(cancelled ? "Cancelled" : string.Empty)}",
            payment.Code, payment.Amount, paidBack ? "by" : "to", supplierName, debtAfter);
        await notifications.NotifyAsync(
            owners,
            new NotificationContent(
                NotificationTypes.SupplierTransaction, "Supplier payment transaction", message,
                new RelatedRecord(RelatedRecordTypes.SupplierPayment, payment.Id, payment.Code)),
            cancellationToken);
        await AlertWhenBudgetNearAsync(owners, cancellationToken);
    }

    /// <summary>
    /// The SMS of supplier transactions has no hard cap (PM decision of 17/11/2026), but the Owner is told once a day, in the app, when the transactions of the day reach 80 % of the configured daily
    /// budget. This transaction is not saved yet, so it is counted on top of the saved ones.
    /// </summary>
    private async Task AlertWhenBudgetNearAsync(IReadOnlyList<Guid> owners, CancellationToken cancellationToken)
    {
        var dailyBudget = budget.Value.DailyBudget;
        if (dailyBudget <= 0 || owners.Count == 0)
        {
            return;
        }

        var now = timeProvider.GetUtcNow();
        var dayStart = new DateTimeOffset(BusinessClock.ToBusinessDate(now).ToDateTime(TimeOnly.MinValue), BusinessClock.UtcOffset);
        var sent = await notificationRows.CountRecordsNotifiedSinceAsync(NotificationTypes.SupplierTransaction, dayStart, cancellationToken) + 1;
        var threshold = (dailyBudget * SupplierSmsBudgetOptions.AlertPercent + 99) / 100;
        if (sent < threshold || await notificationRows.AnyOfTypeSinceAsync(NotificationTypes.SupplierSmsBudget, dayStart, cancellationToken))
        {
            return;
        }

        await notifications.NotifyAsync(
            owners,
            new NotificationContent(NotificationTypes.SupplierSmsBudget, "Supplier payment SMS budget of the day", UserMessages.SupplierSmsBudgetReached(sent, dailyBudget)),
            cancellationToken);
    }
}
