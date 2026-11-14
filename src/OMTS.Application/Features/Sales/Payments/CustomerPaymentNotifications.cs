using OMTS.Application.Common.Interfaces;
using OMTS.Application.Common.Interfaces.Persistence;
using OMTS.Application.Common.Messages;
using OMTS.Application.Features.Notifications;
using OMTS.Application.Features.Purchasing;
using OMTS.Domain.Entities.Sales;

namespace OMTS.Application.Features.Sales.Payments;

/// <summary>
/// BR-DEBT-06: MSG-DEBT-03 to every Owner for each transaction of DEBT-02, always pushed (BR-ACCOUNT-11), also when the Owner did it; one notice for one list of
/// confirmations. The rows join the caller's unit of work; push and SMS go out after its commit (GFR-06).
/// </summary>
public sealed class CustomerPaymentNotifications(
    IZoneRecipientQuery recipients,
    IAccountRepository accounts,
    INotificationDispatcher notifications,
    ICurrentUser currentUser)
{
    public const string Title = "Customer payment transaction";

    /// <param name="debtAfter">The customer debt once the save is done; null without a profile.</param>
    public async Task TransactionAsync(CustomerPayment payment, bool cancelled, decimal? debtAfter, CancellationToken cancellationToken)
    {
        var message = UserMessages.CustomerTransaction(
            await ActorNameAsync(cancellationToken), CustomerPaymentRules.TransactionKind(payment.Kind, cancelled), payment.Code, payment.Amount, payment.Customer?.Name, debtAfter);
        await NotifyAsync(message, new RelatedRecord(RelatedRecordTypes.CustomerPayment, payment.Id, payment.Code), cancellationToken);
    }

    /// <param name="settled">The payments settled from the same list (DEBT-02.6).</param>
    /// <param name="debts">The debt after the save of each profile a cancelled or attached payment belonged to.</param>
    public async Task BatchAsync(
        IReadOnlyList<CustomerPayment> confirmed, IReadOnlyList<CustomerPayment> cancelled, IReadOnlyList<CustomerPayment> settled,
        IReadOnlyList<(string CustomerName, decimal Debt)> debts, CancellationToken cancellationToken)
    {
        var message = UserMessages.CustomerPaymentsBatch(
            await ActorNameAsync(cancellationToken),
            confirmed.Select(p => (p.Code, p.Amount)).ToList(),
            cancelled.Select(p => (p.Code, p.Amount)).ToList(),
            debts,
            settled.Select(p => (p.Code, p.Amount)).ToList());
        // One payment opens it; a list of several opens none (ACCOUNT-08: no related record).
        var all = confirmed.Concat(cancelled).Concat(settled).ToList();
        var record = all.Count == 1 ? new RelatedRecord(RelatedRecordTypes.CustomerPayment, all[0].Id, all[0].Code) : null;
        await NotifyAsync(message, record, cancellationToken);
    }

    private async Task NotifyAsync(UserMessage message, RelatedRecord? record, CancellationToken cancellationToken) =>
        await notifications.NotifyAsync(
            await recipients.GetCooperativeOwnersAsync(cancellationToken),
            new NotificationContent(NotificationTypes.CustomerPaymentTransaction, Title, message, record),
            cancellationToken);

    // The same {user_name} as the supplier transactions (MSG-PURCHASE-04).
    private Task<string> ActorNameAsync(CancellationToken cancellationToken) => PurchasingActorName.OfAsync(currentUser, accounts, cancellationToken);
}
