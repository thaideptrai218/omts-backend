using OMTS.Application.Common.Interfaces;
using OMTS.Application.Common.Interfaces.Persistence;
using OMTS.Application.Common.Messages;
using OMTS.Application.Features.Notifications;
using OMTS.Domain.Entities.Purchasing;
using OMTS.Domain.Enums;

namespace OMTS.Application.Features.Purchasing.Receipts;

/// <summary>
/// BR-PURCHASE-05 for receipts: MSG-PURCHASE-01 to the Owner when a receipt is completed, and MSG-PURCHASE-08 to the
/// Accountant and the Owner when a cancellation brings the supplier debt below zero. The Owner receives them also when the
/// Owner did the action. A draft, a return, another cancellation and a correction send nothing. The rows join the caller's
/// unit of work; push goes out after its commit.
/// </summary>
public sealed class PurchaseReceiptNotifications(
    IZoneRecipientQuery recipients,
    IAccountRepository accounts,
    ISupplierRepository suppliers,
    INotificationDispatcher notifications,
    ICurrentUser currentUser)
{
    public async Task CompletedAsync(PurchaseReceipt receipt, CancellationToken cancellationToken)
    {
        var owners = await recipients.GetOwnersAndRolesAsync([], cancellationToken);
        var supplier = await suppliers.GetForEditAsync(receipt.SupplierId, cancellationToken);
        var message = UserMessages.ReceiptCompleted(
            await ActorNameAsync(cancellationToken), receipt.Code, supplier?.Name ?? string.Empty, receipt.Lines.Count, PurchaseMoney.Total(receipt.Lines));
        await notifications.NotifyAsync(
            owners,
            new NotificationContent(NotificationTypes.PurchaseReceiptCompleted, "New purchase receipt", message, Related(receipt)),
            cancellationToken);
    }

    /// <param name="advanceAmount">What the cooperative has now paid the supplier in advance (positive).</param>
    public async Task PaidInAdvanceAsync(PurchaseReceipt receipt, string supplierName, decimal advanceAmount, CancellationToken cancellationToken)
    {
        var accountantsAndOwners = await recipients.GetOwnersAndRolesAsync([StaffRole.Accountant], cancellationToken);
        var message = UserMessages.ReceiptCancelledPaidInAdvance(await ActorNameAsync(cancellationToken), receipt.Code, supplierName, advanceAmount);
        await notifications.NotifyAsync(
            accountantsAndOwners,
            new NotificationContent(NotificationTypes.PurchaseReceiptCancelledPaidInAdvance, "Supplier paid in advance", message, Related(receipt)),
            cancellationToken);
    }

    private static RelatedRecord Related(PurchaseReceipt receipt) => new(RelatedRecordTypes.PurchaseReceipt, receipt.Id, receipt.Code);

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
