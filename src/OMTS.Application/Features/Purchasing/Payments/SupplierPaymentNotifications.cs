using OMTS.Application.Common.Interfaces;
using OMTS.Application.Common.Interfaces.Persistence;
using OMTS.Application.Common.Messages;
using OMTS.Application.Features.Notifications;
using OMTS.Domain.Entities.Purchasing;
using OMTS.Domain.Enums;

namespace OMTS.Application.Features.Purchasing.Payments;

/// <summary>
/// BR-PURCHASE-05 for payments: MSG-PURCHASE-04 to every Owner for a payment, money paid back and a cancelled payment, always
/// pushed, also when the Owner did it. The rows join the caller's unit of work; push goes out after its commit.
/// </summary>
public sealed class SupplierPaymentNotifications(
    IZoneRecipientQuery recipients,
    IAccountRepository accounts,
    INotificationDispatcher notifications,
    ICurrentUser currentUser)
{
    /// <param name="cancelled">The payment was just cancelled.</param>
    /// <param name="debtAfter">The supplier debt once this save is done.</param>
    public async Task TransactionAsync(SupplierPayment payment, string supplierName, bool cancelled, decimal debtAfter, CancellationToken cancellationToken)
    {
        var owners = await recipients.GetOwnersAndRolesAsync([], cancellationToken);
        var paidBack = payment.Kind == SupplierPaymentKind.PaidBack;
        var kindText = (cancelled, paidBack) switch
        {
            (false, false) => "payment",
            (false, true) => "money paid back",
            (true, false) => "the cancellation of payment",
            (true, true) => "the cancellation of money paid back",
        };
        var kindCode = $"{(paidBack ? "PaidBack" : "Payment")}{(cancelled ? "Cancelled" : string.Empty)}";
        var text = UserMessages.SupplierTransactionText(kindText, payment.Code, payment.Amount, paidBack ? "by" : "to", supplierName);
        var message = UserMessages.SupplierTransaction(
            await ActorNameAsync(cancellationToken), kindCode, text, supplierName, payment.Code, payment.Amount, debtAfter);
        await notifications.NotifyAsync(
            owners,
            new NotificationContent(
                NotificationTypes.SupplierTransaction, "Supplier payment transaction", message,
                new RelatedRecord(RelatedRecordTypes.SupplierPayment, payment.Id, payment.Code)),
            cancellationToken);
    }

    private async Task<string> ActorNameAsync(CancellationToken cancellationToken)
    {
        if (currentUser.AccountId is not { } accountId)
        {
            return "The system";
        }

        var names = await accounts.GetDisplayNamesAsync([accountId], cancellationToken);
        return names.GetValueOrDefault(accountId) ?? "A user";
    }
}
