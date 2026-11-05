using OMTS.Application.Common;
using OMTS.Application.Common.Concurrency;
using OMTS.Application.Common.Interfaces;
using OMTS.Application.Common.Interfaces.Persistence;
using OMTS.Application.Common.Messages;
using OMTS.Application.Common.Results;
using OMTS.Application.Features.Audit;
using OMTS.Application.Features.Audit.Models;
using OMTS.Application.Features.Purchasing.Payments;
using OMTS.Domain.Constants;
using OMTS.Domain.Entities.Purchasing;

namespace OMTS.Application.Features.Purchasing.Receipts;

/// <summary>
/// PURCHASE-06 – remove a Draft (PURCHASE-06.1) or cancel a Completed receipt with a reason, by the person who recorded it
/// or the Owner (BR-PURCHASE-09). A receipt whose seed lot was seeded, or that has a purchase return, cannot be cancelled
/// (MSG-PURCHASE-09). Payments are not linked to receipts and are never cancelled with one, so the debt may go below zero
/// (MSG-PURCHASE-07 before, MSG-PURCHASE-08 after).
/// </summary>
public sealed class PurchaseReceiptCancellationService(
    IPurchaseReceiptRepository receipts,
    PurchaseReceiptViews views,
    PurchaseReceiptAccess access,
    PurchaseReceiptNotifications notifications,
    SupplierDebtService debts,
    IStatusCatalog statusCatalog,
    RowVersionGuard rowVersions,
    AuditLogService audit,
    IUnitOfWork unitOfWork,
    TimeProvider timeProvider)
{
    private const string RecordType = nameof(PurchaseReceipt);

    /// <summary>PURCHASE-06.1: deletes a Draft and its lines; the removal stays in the audit log only.</summary>
    public async Task<Result<Done>> RemoveAsync(Guid receiptId, CancellationToken cancellationToken)
    {
        var receipt = await receipts.GetForEditAsync(receiptId, cancellationToken);
        if (receipt is null)
        {
            return AppError.NotFound;
        }

        if (!await access.MayChangeAsync(receipt, cancellationToken))
        {
            return AppError.Forbidden;
        }

        if (receipt.StatusId != StatusIds.PurchaseReceipt.Draft)
        {
            // A Completed receipt is cancelled with a reason, never removed.
            return WrongStatus(receipt);
        }

        var before = PurchaseReceiptService.Snapshot(receipt);
        foreach (var line in receipt.Lines.ToList())
        {
            receipt.Lines.Remove(line);
            receipts.RemoveLine(line);
        }

        receipts.Remove(receipt);
        audit.Record(new AuditEntry("PurchaseReceipt.Removed", RecordType) { RecordId = receipt.Id, RecordCode = receipt.Code, Before = before });
        await unitOfWork.SaveChangesAsync(cancellationToken);
        return Done.Value;
    }

    /// <summary>PURCHASE-06 steps 1–2: whether it can be cancelled, and the supplier debt before and after.</summary>
    public async Task<Result<CancelPreview>> PreviewAsync(Guid receiptId, CancellationToken cancellationToken)
    {
        var (receipt, refused) = await CancellableAsync(receiptId, cancellationToken);
        if (refused is not null)
        {
            return refused;
        }

        var blocked = await BlockAsync(receipt!, cancellationToken);
        var (before, after) = await DebtsAsync(receipt!, cancellationToken);
        var warning = after < 0 ? UserMessages.PaidInAdvanceAfterCancel(receipt!.Supplier.Name, -after) : null;
        return new CancelPreview(blocked is null, blocked, before, after, Math.Max(0, -after), warning?.Text, warning?.Code);
    }

    public async Task<Result<PurchaseReceiptView>> CancelAsync(Guid receiptId, CancelReceiptRequest request, CancellationToken cancellationToken)
    {
        var (receipt, refused) = await CancellableAsync(receiptId, cancellationToken);
        if (refused is not null)
        {
            return refused;
        }

        if (await BlockAsync(receipt!, cancellationToken) is { } blocked)
        {
            return AppError.Conflict(
                UserMessages.ReceiptCannotBeCancelled(receipt!.Code, CauseText(blocked)),
                new Dictionary<string, object?> { ["cause"] = blocked.Kind, ["causeCode"] = blocked.Code });
        }

        var reason = request.Reason?.Trim() ?? string.Empty;
        if (reason.Length is 0 or > 1000)
        {
            return AppError.Invalid("Reason");
        }

        if (request.RowVersion is not null && await rowVersions.CheckAsync(receipt!, request.RowVersion, cancellationToken) is { } conflict)
        {
            return conflict;
        }

        var (_, debtAfter) = await DebtsAsync(receipt!, cancellationToken);
        receipt!.StatusId = StatusIds.PurchaseReceipt.Cancelled;
        receipt.CancelledAt = timeProvider.GetUtcNow();
        receipt.CancelReason = reason;
        audit.Record(new AuditEntry("PurchaseReceipt.Cancelled", RecordType)
        {
            RecordId = receipt.Id,
            RecordCode = receipt.Code,
            Before = new { Status = statusCatalog.CodeOf(StatusIds.PurchaseReceipt.Completed) },
            After = new { Status = statusCatalog.CodeOf(receipt.StatusId) },
            Reason = reason,
        });
        if (debtAfter < 0)
        {
            await notifications.PaidInAdvanceAsync(receipt, receipt.Supplier.Name, -debtAfter, cancellationToken);
        }

        await unitOfWork.SaveChangesAsync(cancellationToken);
        return await views.ViewAsync(receipt.Id, cancellationToken);
    }

    /// <summary>
    /// PURCHASE-06.0.E1: the first seed lot of the receipt that has been seeded, else its first purchase return; null when
    /// it can be cancelled. Also tells the Owner's correction (PURCHASE-06.2) that the receipt can no longer be cancelled.
    /// </summary>
    public async Task<CancelBlock?> BlockAsync(PurchaseReceipt receipt, CancellationToken cancellationToken)
    {
        var seeded = await receipts.SeededAsync(receipt.Lines.Select(l => l.Id).ToList(), cancellationToken);
        if (receipt.Lines.OrderBy(l => l.LineNumber).FirstOrDefault(l => seeded.ContainsKey(l.Id)) is { } seededLine)
        {
            return Block("Seeded", DocumentCodes.SeedLotCode(receipt.Code, seededLine.LineNumber), receipt);
        }

        return receipt.Returns.OrderBy(r => r.ReturnDate).ThenBy(r => r.Code).FirstOrDefault() is { } purchaseReturn
            ? Block("Returned", purchaseReturn.Code, receipt)
            : null;
    }

    private static CancelBlock Block(string kind, string code, PurchaseReceipt receipt)
    {
        var block = new CancelBlock(kind, code, string.Empty, MessageCodes.Purchase09);
        return block with { Message = UserMessages.ReceiptCannotBeCancelled(receipt.Code, CauseText(block)).Text };
    }

    private static string CauseText(CancelBlock block) =>
        block.Kind == "Seeded" ? $"seed lot {block.Code} has already been seeded" : $"it has purchase return {block.Code}";

    /// <summary>The receipt, cancellable by the caller (own or Owner) and Completed (MSG-GEN-09 for a Draft or a Cancelled one).</summary>
    private async Task<(PurchaseReceipt? Receipt, AppError? Refused)> CancellableAsync(Guid receiptId, CancellationToken cancellationToken)
    {
        var receipt = await receipts.GetForEditAsync(receiptId, cancellationToken);
        if (receipt is null)
        {
            return (null, AppError.NotFound);
        }

        if (!await access.MayChangeAsync(receipt, cancellationToken))
        {
            return (receipt, AppError.Forbidden);
        }

        // A Draft is removed instead (PURCHASE-06.1); a Cancelled receipt stays cancelled.
        return (receipt, receipt.StatusId == StatusIds.PurchaseReceipt.Completed ? null : WrongStatus(receipt));
    }

    /// <summary>
    /// The supplier debt now and after the cancellation: the receipt leaves the debt, and the payments, not linked to it, stay
    /// recorded, so they still lower it (BR-PURCHASE-04, BR-PURCHASE-09).
    /// </summary>
    private async Task<(decimal Before, decimal After)> DebtsAsync(PurchaseReceipt receipt, CancellationToken cancellationToken)
    {
        // Every return blocks a cancel today. When P4 lets a receipt with only Cancelled returns be cancelled, this formula
        // must count the returns that are not cancelled only.
        var before = await debts.DebtAsync(receipt.SupplierId, cancellationToken);
        return (before, before - PurchaseMoney.Of(receipt).DebtShare);
    }

    private AppError WrongStatus(PurchaseReceipt receipt) =>
        AppError.Conflict(UserMessages.WrongStatus($"Purchase receipt {receipt.Code}", statusCatalog.DisplayNameOf(receipt.StatusId)));
}
