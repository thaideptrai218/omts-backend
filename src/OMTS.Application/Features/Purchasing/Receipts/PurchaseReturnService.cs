using OMTS.Application.Common;
using OMTS.Application.Common.Concurrency;
using OMTS.Application.Common.Constants;
using OMTS.Application.Common.Interfaces;
using OMTS.Application.Common.Interfaces.Persistence;
using OMTS.Application.Common.Messages;
using OMTS.Application.Common.Results;
using OMTS.Application.Features.Audit;
using OMTS.Application.Features.Audit.Models;
using OMTS.Application.Features.WaterQuality.Dtos;
using OMTS.Domain.Constants;
using OMTS.Domain.Entities.Purchasing;

namespace OMTS.Application.Features.Purchasing.Receipts;

/// <summary>
/// PURCHASE-02 – goods sent back from a Completed receipt, after the Owner approved it in person (the Zone Manager confirms it,
/// MSG-PURCHASE-02), and the Owner's cancellation of one recorded by mistake. A line never returns more than received − seeded
/// − returned, also under parallel saves: the receipt row is part of each save, and the loser answers MSG06. Receipts have no
/// zone: the API checks the role (BR-FARM-08). A return sends no notification (BR-PURCHASE-05, BR-PURCHASE-07).
/// The answers report no supplier debt, so the supplier row takes no part in the save, unlike a payment (PM 03/11).
/// </summary>
public sealed class PurchaseReturnService(
    IPurchaseReceiptRepository receipts,
    PurchaseReceiptViews views,
    PurchaseReceiptAccess access,
    IDocumentNumberAllocator numbers,
    IAccountRepository accounts,
    ICurrentUser currentUser,
    IStatusCatalog statusCatalog,
    RowVersionGuard rowVersions,
    AuditLogService audit,
    IUnitOfWork unitOfWork,
    TimeProvider timeProvider)
{
    private const string RecordType = nameof(PurchaseReturn);

    public async Task<Result<PurchaseReceiptView>> CreateAsync(Guid receiptId, CreatePurchaseReturnRequest request, CancellationToken cancellationToken)
    {
        var receipt = await receipts.GetForEditAsync(receiptId, cancellationToken);
        if (receipt is null)
        {
            return AppError.NotFound;
        }

        if (receipt.StatusId != StatusIds.PurchaseReceipt.Completed)
        {
            return AppError.Conflict(UserMessages.WrongStatus($"Purchase receipt {receipt.Code}", statusCatalog.DisplayNameOf(receipt.StatusId)));
        }

        var invalid = PurchaseReturnRules.InvalidFields(request, receipt);
        if (invalid.Count > 0)
        {
            return AppError.InvalidFields(invalid.Distinct().ToList());
        }

        var inputs = request.Lines!;
        var seeded = await receipts.SeededAsync(receipt.Lines.Select(l => l.Id).ToList(), cancellationToken);
        if (PurchaseReturnRules.QuantityError(receipt, inputs, seeded, PurchaseReceiptViews.ReturnedQuantities(receipt)) is { } quantityError)
        {
            return quantityError;
        }

        // BR-PURCHASE-02, PM 03/11: each line half up to the dong at today's unit price, the total the sum of the lines; kept as recorded.
        var lines = inputs.Select(input =>
        {
            var line = receipt.Lines.First(l => l.Id == input.ReceiptLineId);
            return new PurchaseReturnLine
            {
                ReceiptLineId = line.Id,
                Quantity = input.Quantity!.Value,
                ReturnValue = PurchaseMoney.LineAmount(input.Quantity, line.UnitPrice),
            };
        }).ToList();

        var isOwner = await access.IsCooperativeOwnerAsync(cancellationToken);
        if (!isOwner && !request.OwnerApproved)
        {
            return PurchaseReturnRules.OwnerApprovalConfirmation(lines.Sum(l => l.ReturnValue), receipt.Supplier.Name);
        }

        // Taken once the return is valid and confirmed, so a refused request spends no number; never given twice.
        var purchaseReturn = new PurchaseReturn
        {
            Code = DocumentCodes.ReturnCode(await numbers.NextAsync(DocumentSequenceKeys.PurchaseReturn, cancellationToken)),
            ReceiptId = receipt.Id,
            ReturnDate = timeProvider.Today(),
            Reason = request.Reason!.Trim(),
            RecordedByAccountId = currentUser.AccountId!.Value,
        };
        lines.ForEach(purchaseReturn.Lines.Add);
        receipt.Returns.Add(purchaseReturn);
        receipts.AddReturn(purchaseReturn);
        unitOfWork.MarkChanged(receipt);
        audit.Record(new AuditEntry("PurchaseReturn.Created", RecordType)
        {
            RecordId = purchaseReturn.Id,
            RecordCode = purchaseReturn.Code,
            After = new
            {
                ReceiptId = receipt.Id, ReceiptCode = receipt.Code, purchaseReturn.ReturnDate,
                // The Zone Manager's answer to MSG-PURCHASE-02; the Owner is not asked.
                OwnerApprovalConfirmed = !isOwner,
                Lines = lines.Select(l => new { l.ReceiptLineId, l.Quantity, receipt.Lines.First(x => x.Id == l.ReceiptLineId).UnitPrice, l.ReturnValue }),
            },
            Reason = purchaseReturn.Reason,
        });
        await unitOfWork.SaveChangesAsync(cancellationToken);
        return await views.ViewAsync(receipt.Id, cancellationToken);
    }

    /// <summary>
    /// PURCHASE-02.1 (the API lets the Owner only): Recorded → Cancelled with a reason. It frees its quantities and no longer
    /// counts in the supplier debt or a raft's material cost, also in past periods, and no longer blocks the receipt's cancel.
    /// </summary>
    public async Task<Result<PurchaseReceiptView>> CancelAsync(Guid returnId, CancelPurchaseReturnRequest request, CancellationToken cancellationToken)
    {
        var found = await receipts.GetReturnAsync(returnId, cancellationToken);
        if (found is null)
        {
            return AppError.NotFound;
        }

        var receipt = await receipts.GetForEditAsync(found.ReceiptId, cancellationToken);
        var purchaseReturn = receipt!.Returns.First(r => r.Id == returnId);
        if (purchaseReturn.StatusId != StatusIds.PurchaseReturn.Recorded)
        {
            return AppError.Conflict(UserMessages.WrongStatus($"Purchase return {purchaseReturn.Code}", statusCatalog.DisplayNameOf(purchaseReturn.StatusId)));
        }

        var reason = request.Reason?.Trim() ?? string.Empty;
        if (reason.Length is 0 or > PurchaseReturnRules.MaxReasonLength)
        {
            return AppError.Invalid("Reason");
        }

        if (request.RowVersion is not null && await rowVersions.CheckAsync(purchaseReturn, request.RowVersion, cancellationToken) is { } conflict)
        {
            return conflict;
        }

        purchaseReturn.StatusId = StatusIds.PurchaseReturn.Cancelled;
        purchaseReturn.CancelledAt = timeProvider.GetUtcNow();
        purchaseReturn.CancelReason = reason;
        unitOfWork.MarkChanged(receipt);
        audit.Record(new AuditEntry("PurchaseReturn.Cancelled", RecordType)
        {
            RecordId = purchaseReturn.Id,
            RecordCode = purchaseReturn.Code,
            Before = new { Status = statusCatalog.CodeOf(StatusIds.PurchaseReturn.Recorded) },
            After = new { Status = statusCatalog.CodeOf(purchaseReturn.StatusId) },
            Reason = reason,
        });
        await unitOfWork.SaveChangesAsync(cancellationToken);
        return await views.ViewAsync(receipt.Id, cancellationToken);
    }

    public async Task<Result<PurchaseReturnView>> GetAsync(Guid returnId, CancellationToken cancellationToken)
    {
        var found = await receipts.GetReturnAsync(returnId, cancellationToken);
        if (found is null)
        {
            return AppError.NotFound;
        }

        var names = await accounts.GetDisplayNamesAsync([found.RecordedByAccountId], cancellationToken);
        return new PurchaseReturnView(
            found.Id, found.Code, found.ReceiptId, found.Receipt.Code, found.ReturnDate, found.Reason, found.StatusId, statusCatalog.DisplayNameOf(found.StatusId),
            found.CancelledAt, found.CancelReason, new AccountRef(found.RecordedByAccountId, names.GetValueOrDefault(found.RecordedByAccountId) ?? string.Empty),
            found.Lines.Sum(l => l.ReturnValue),
            found.Lines.Select(l => new PurchaseReturnLineView(
                l.ReceiptLineId, l.ReceiptLine.Item?.Code ?? string.Empty, l.ReceiptLine.Item?.Name ?? string.Empty, l.ReceiptLine.Unit, l.Quantity, l.ReturnValue)).ToList(),
            RowVersionGuard.Encode(found.RowVersion));
    }
}
