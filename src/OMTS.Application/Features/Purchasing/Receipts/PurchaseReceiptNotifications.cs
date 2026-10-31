using OMTS.Application.Common.Interfaces;
using OMTS.Application.Common.Interfaces.Persistence;
using OMTS.Application.Common.Messages;
using OMTS.Application.Features.Notifications;
using OMTS.Domain.Entities.Purchasing;

namespace OMTS.Application.Features.Purchasing.Receipts;

/// <summary>
/// BR-PURCHASE-05 for receipts: MSG-PURCHASE-01 to the Owner when a receipt is completed. The Owner receives it also when
/// the Owner did the action. A draft, a return, a cancellation and a correction send nothing. The rows join the caller's
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
