using OMTS.Application.Common.Concurrency;
using OMTS.Application.Common.Interfaces;
using OMTS.Application.Common.Interfaces.Persistence;
using OMTS.Application.Common.Messages;
using OMTS.Application.Common.Results;
using OMTS.Application.Features.Audit;
using OMTS.Application.Features.Audit.Models;
using OMTS.Domain.Constants;
using OMTS.Domain.Entities.Purchasing;

namespace OMTS.Application.Features.Purchasing.Receipts;

/// <summary>
/// PURCHASE-06 – cancel a Completed receipt with a reason, by the person who recorded it or the Owner (BR-PURCHASE-09). A
/// receipt whose seed lot was seeded, or that has a purchase return, cannot be cancelled (MSG-PURCHASE-09). Payments are
/// never cancelled with it: those still allocated to it are kept as money the supplier holds.
/// </summary>
public sealed class PurchaseReceiptCancellationService(
    IPurchaseReceiptRepository receipts,
    PurchaseReceiptViews views,
    PurchaseReceiptAccess access,
    IStatusCatalog statusCatalog,
    RowVersionGuard rowVersions,
    AuditLogService audit,
    IUnitOfWork unitOfWork,
    TimeProvider timeProvider)
{
    private const string RecordType = nameof(PurchaseReceipt);

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

        receipt!.StatusId = StatusIds.PurchaseReceipt.Cancelled;
        receipt.CancelledAt = timeProvider.GetUtcNow();
        receipt.CancelReason = reason;
        foreach (var payment in receipt.Payments.Where(p => p.StatusId == StatusIds.SupplierPayment.Recorded).ToList())
        {
            // BR-PURCHASE-09: never cancelled; it stays money the supplier holds until the payments are untied from receipts.
            receipt.Payments.Remove(payment);
            payment.ReceiptId = null;
            audit.Record(new AuditEntry("SupplierPayment.Detached", nameof(SupplierPayment))
            {
                RecordId = payment.Id, Before = new { ReceiptId = receipt.Id }, After = new { ReceiptId = (Guid?)null }, Reason = reason,
            });
        }

        audit.Record(new AuditEntry("PurchaseReceipt.Cancelled", RecordType)
        {
            RecordId = receipt.Id,
            RecordCode = receipt.Code,
            Before = new { Status = statusCatalog.CodeOf(StatusIds.PurchaseReceipt.Completed) },
            After = new { Status = statusCatalog.CodeOf(receipt.StatusId) },
            Reason = reason,
        });
        await unitOfWork.SaveChangesAsync(cancellationToken);
        return await views.ViewAsync(receipt.Id, cancellationToken);
    }

    /// <summary>
    /// PURCHASE-06.0.E1: the first seed lot of the receipt that has been seeded, else its first purchase return; null when
    /// it can be cancelled.
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

        return (receipt, receipt.StatusId == StatusIds.PurchaseReceipt.Completed ? null : WrongStatus(receipt));
    }

    private AppError WrongStatus(PurchaseReceipt receipt) =>
        AppError.Conflict(UserMessages.WrongStatus($"Purchase receipt {receipt.Code}", statusCatalog.DisplayNameOf(receipt.StatusId)));
}
