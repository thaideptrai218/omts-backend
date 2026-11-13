using OMTS.Application.Common.Interfaces;
using OMTS.Application.Common.Interfaces.Persistence;
using OMTS.Application.Common.Messages;
using OMTS.Application.Features.Notifications;
using OMTS.Application.Features.Purchasing;
using OMTS.Application.Features.Sales.Debts;
using OMTS.Domain.Entities.Sales;
using OMTS.Domain.Enums;

namespace OMTS.Application.Features.Sales.Invoices;

/// <summary>
/// SALE-07 step 10 and SALE-08 step 8: MSG-SALE-18 for each profile left with a debt below zero, to the Owners and the Accountants (the Accountant is also asked to pay it back). The rows join the
/// caller's save and are pushed after it commits (GFR-06). A debt that stays at or above zero tells no one.
/// </summary>
public sealed class SalesInvoiceOwedBackNotifier(IZoneRecipientQuery recipients, IAccountRepository accounts, INotificationDispatcher notifications, ICurrentUser currentUser)
{
    public const string Title = "Money owed back to a customer";

    /// <param name="actionText">"corrected" or "cancelled".</param>
    internal async Task NotifyAsync(SalesInvoice invoice, IReadOnlyList<CustomerDebtAfterChange> debts, string actionText, CancellationToken cancellationToken)
    {
        var owedBack = debts.Where(d => d.DebtAfter < 0m).ToList();
        if (owedBack.Count == 0)
        {
            return;
        }

        var owners = await recipients.GetCooperativeOwnersAsync(cancellationToken);
        var accountants = (await recipients.GetOwnersAndRolesAsync([StaffRole.Accountant], cancellationToken)).Except(owners).ToList();
        var userName = await PurchasingActorName.OfAsync(currentUser, accounts, cancellationToken);
        var record = new RelatedRecord(RelatedRecordTypes.SalesInvoice, invoice.Id, invoice.Code);
        foreach (var debt in owedBack)
        {
            var amount = CustomerDebtMath.OwedBack(debt.DebtAfter);
            await notifications.NotifyAsync(owners, Content(userName, actionText, invoice.Code, debt.Customer, amount, forAccountant: false, record), cancellationToken);
            if (accountants.Count > 0)
            {
                await notifications.NotifyAsync(accountants, Content(userName, actionText, invoice.Code, debt.Customer, amount, forAccountant: true, record), cancellationToken);
            }
        }
    }

    private static NotificationContent Content(string userName, string actionText, string invoiceCode, Customer customer, decimal amount, bool forAccountant, RelatedRecord record) =>
        new(NotificationTypes.CustomerMoneyOwedBack, Title, UserMessages.MoneyOwedBackNotice(userName, actionText, invoiceCode, customer.Name, amount, forAccountant), record);
}
