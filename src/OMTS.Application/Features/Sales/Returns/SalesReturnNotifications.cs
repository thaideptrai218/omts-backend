using OMTS.Application.Common.Interfaces;
using OMTS.Application.Common.Interfaces.Persistence;
using OMTS.Application.Common.Messages;
using OMTS.Application.Features.Notifications;
using OMTS.Application.Features.Purchasing;
using OMTS.Domain.Entities.Sales;
using OMTS.Domain.Enums;

namespace OMTS.Application.Features.Sales.Returns;

/// <summary>
/// CLAIM-03 step 9, MSG-CLAIM-05: to the Cooperative Owners; when the customer debt is now below zero also to the Accountants, as the owed-back type, who pay the money back in DEBT-02. The rows join
/// the save and are pushed after it commits, so a failed push never undoes the return (GFR-06). A cancellation sends none (BR-CLAIM-03).
/// </summary>
public sealed class SalesReturnNotifications(IZoneRecipientQuery recipients, IAccountRepository accounts, INotificationDispatcher notifications, ICurrentUser currentUser)
{
    private const string Title = "New sales return";

    public async Task NotifyAsync(SalesInvoice invoice, SalesReturn salesReturn, decimal debtAfter, CancellationToken cancellationToken)
    {
        var userName = await PurchasingActorName.OfAsync(currentUser, accounts, cancellationToken);
        var backForSale = salesReturn.Lines.Where(l => l.Outcome == SalesReturnOutcome.BackForSale).Sum(l => l.ReturnedKg);
        var discarded = salesReturn.Lines.Sum(l => l.DiscardedKg);
        var record = new RelatedRecord(RelatedRecordTypes.SalesInvoice, invoice.Id, invoice.Code);
        var customerName = invoice.Customer!.Name;
        UserMessage Text(bool forAccountant) =>
            UserMessages.SalesReturnRecorded(userName, salesReturn.Code, invoice.Code, customerName, salesReturn.Value, backForSale, discarded, debtAfter, forAccountant);

        var owners = await recipients.GetCooperativeOwnersAsync(cancellationToken);
        await notifications.NotifyAsync(owners, new NotificationContent(NotificationTypes.SalesReturnRecorded, Title, Text(forAccountant: false), record), cancellationToken);
        if (debtAfter < 0m)
        {
            var accountants = (await recipients.GetOwnersAndRolesAsync([StaffRole.Accountant], cancellationToken)).Except(owners).ToList();
            if (accountants.Count > 0)
            {
                await notifications.NotifyAsync(accountants, new NotificationContent(NotificationTypes.CustomerMoneyOwedBack, Title, Text(forAccountant: true), record), cancellationToken);
            }
        }
    }
}
