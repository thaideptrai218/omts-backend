using OMTS.Application.Common;
using OMTS.Application.Common.Concurrency;
using OMTS.Application.Common.Exceptions;
using OMTS.Application.Common.Interfaces;
using OMTS.Application.Common.Interfaces.Persistence;
using OMTS.Application.Common.Messages;
using OMTS.Application.Common.Results;
using OMTS.Application.Features.Audit;
using OMTS.Application.Features.Audit.Models;
using OMTS.Domain.Constants;
using OMTS.Domain.Entities.Platform;
using OMTS.Domain.Entities.Purchasing;
using OMTS.Domain.Enums;

namespace OMTS.Application.Features.Purchasing.Receipts;

/// <summary>
/// PURCHASE-01 – purchase receipts saved as Draft or Completed. Receipts have no zone (the API checks the role); a Draft is
/// continued or completed only by the person who recorded it or the Owner (BR-PURCHASE-03). Completing makes each Seed
/// line a seed lot (GBR-04) and notifies the Owner (MSG-PURCHASE-01). A Completed receipt is never edited here.
/// </summary>
public sealed class PurchaseReceiptService(
    IPurchaseReceiptRepository receipts,
    PurchaseReceiptValidator validator,
    PurchaseReceiptViews views,
    PurchaseReceiptAccess access,
    PurchaseReceiptNotifications notifications,
    ICurrentUser currentUser,
    IStatusCatalog statusCatalog,
    RowVersionGuard rowVersions,
    AuditLogService audit,
    IUnitOfWork unitOfWork,
    TimeProvider timeProvider)
{
    private const string RecordType = nameof(PurchaseReceipt);

    // Parallel saves read the same latest code; each retry reads it again, so a handful of attempts settles any race.
    private const int MaxCodeAttempts = 10;

    public async Task<Result<PurchaseReceiptView>> GetAsync(Guid receiptId, CancellationToken cancellationToken) =>
        await receipts.GetForEditAsync(receiptId, cancellationToken) is { } receipt
            ? await views.ViewAsync(receipt.Id, cancellationToken)
            : AppError.NotFound;

    public async Task<Result<PurchaseReceiptView>> CreateAsync(SavePurchaseReceiptRequest request, CancellationToken cancellationToken)
    {
        var (refs, invalid) = await validator.CheckDraftAsync(request, cancellationToken);
        if (invalid is not null)
        {
            return invalid;
        }

        var receipt = new PurchaseReceipt { RecordedByAccountId = currentUser.AccountId!.Value };
        Apply(receipt, request, refs);
        if (request.Complete && await validator.CheckCompleteAsync(receipt, refs, cancellationToken) is { } error)
        {
            return error;
        }

        receipts.Add(receipt);
        var events = new List<AuditEvent>
        {
            audit.Record(new AuditEntry("PurchaseReceipt.Created", RecordType) { RecordId = receipt.Id, After = Snapshot(receipt) }),
        };
        if (request.Complete)
        {
            events.Add(MarkCompleted(receipt));
        }

        // BR-PURCHASE-03: the code is given at the first save. The notification names it, so it is added once the code is final.
        await SaveWithCodeAsync(receipt, events, cancellationToken);
        if (request.Complete)
        {
            await notifications.CompletedAsync(receipt, cancellationToken);
            await unitOfWork.SaveChangesAsync(cancellationToken);
        }

        return await views.ViewAsync(receipt.Id, cancellationToken);
    }

    /// <summary>Continues a Draft (PURCHASE-01.3): replaces its fields and lines; with <c>complete</c> it is completed in the same save.</summary>
    public async Task<Result<PurchaseReceiptView>> UpdateAsync(Guid receiptId, SavePurchaseReceiptRequest request, CancellationToken cancellationToken)
    {
        var receipt = await receipts.GetForEditAsync(receiptId, cancellationToken);
        if (receipt is null)
        {
            return AppError.NotFound;
        }

        if (await EditableDraftAsync(receipt, request.RowVersion, requireRowVersion: true, cancellationToken) is { } refused)
        {
            return refused;
        }

        var (refs, invalid) = await validator.CheckDraftAsync(request, cancellationToken);
        if (invalid is not null)
        {
            return invalid;
        }

        var before = Snapshot(receipt);
        foreach (var line in receipt.Lines.ToList())
        {
            receipt.Lines.Remove(line);
            receipts.RemoveLine(line);
        }

        Apply(receipt, request, refs);
        // Lines are rows of their own: a lines-only edit must still give the receipt a new row version, so a second
        // editor holding the old one gets MSG06 instead of overwriting it.
        unitOfWork.MarkChanged(receipt);
        if (request.Complete)
        {
            if (await validator.CheckCompleteAsync(receipt, refs, cancellationToken) is { } error)
            {
                return error;
            }

            MarkCompleted(receipt);
            await notifications.CompletedAsync(receipt, cancellationToken);
        }

        var (changedBefore, changedAfter) = AuditDiff.Changed(before, Snapshot(receipt));
        if (changedAfter is not null)
        {
            audit.Record(new AuditEntry("PurchaseReceipt.Updated", RecordType)
            {
                RecordId = receipt.Id, RecordCode = receipt.Code, Before = changedBefore, After = changedAfter,
            });
        }

        await unitOfWork.SaveChangesAsync(cancellationToken);
        return await views.ViewAsync(receipt.Id, cancellationToken);
    }

    /// <summary>PURCHASE-01 steps 6–9 on a saved Draft; the row version is optional (the status guards a second complete).</summary>
    public async Task<Result<PurchaseReceiptView>> CompleteAsync(Guid receiptId, CompleteReceiptRequest request, CancellationToken cancellationToken)
    {
        var receipt = await receipts.GetForEditAsync(receiptId, cancellationToken);
        if (receipt is null)
        {
            return AppError.NotFound;
        }

        if (await EditableDraftAsync(receipt, request.RowVersion, requireRowVersion: false, cancellationToken) is { } refused)
        {
            return refused;
        }

        var refs = await validator.LoadAsync(receipt.Lines.Select(l => l.ItemId), receipt.Lines.Select(l => l.RaftId), cancellationToken);
        if (await validator.CheckCompleteAsync(receipt, refs, cancellationToken) is { } error)
        {
            return error;
        }

        MarkCompleted(receipt);
        await notifications.CompletedAsync(receipt, cancellationToken);
        await unitOfWork.SaveChangesAsync(cancellationToken);
        return await views.ViewAsync(receipt.Id, cancellationToken);
    }

    /// <summary>A Draft that the caller recorded (or any, for the Owner); MSG-GEN-09 for another status.</summary>
    private async Task<AppError?> EditableDraftAsync(PurchaseReceipt receipt, string? rowVersion, bool requireRowVersion, CancellationToken cancellationToken)
    {
        if (!await access.MayChangeAsync(receipt, cancellationToken))
        {
            return AppError.Forbidden;
        }

        if (receipt.StatusId != StatusIds.PurchaseReceipt.Draft)
        {
            return AppError.Conflict(UserMessages.WrongStatus($"Purchase receipt {receipt.Code}", statusCatalog.DisplayNameOf(receipt.StatusId)));
        }

        // A draft edit replaces every line, so it must say which version it read (MSG-GEN-01 when missing).
        return rowVersion is null && !requireRowVersion ? null : await rowVersions.CheckAsync(receipt, rowVersion, cancellationToken);
    }

    /// <summary>Draft → Completed (BR-PURCHASE-03): from now on each Seed line is a seed lot and the total counts in the supplier debt.</summary>
    private AuditEvent MarkCompleted(PurchaseReceipt receipt)
    {
        receipt.StatusId = StatusIds.PurchaseReceipt.Completed;
        receipt.CompletedAt = timeProvider.GetUtcNow();
        return audit.RecordStatusChange(RecordType, receipt.Id, receipt.Code, StatusIds.PurchaseReceipt.Draft, StatusIds.PurchaseReceipt.Completed);
    }

    /// <summary>
    /// Saves a new receipt with the next PN code (GBR-04); when a parallel save took that code (unique index), the code is
    /// read again and the next one is used for the receipt and its audit rows.
    /// </summary>
    private async Task SaveWithCodeAsync(PurchaseReceipt receipt, IReadOnlyList<AuditEvent> events, CancellationToken cancellationToken)
    {
        for (var attempt = 1; ; attempt++)
        {
            receipt.Code = DocumentCodes.ReceiptCode(await receipts.LatestReceiptNumberAsync(cancellationToken) + 1);
            foreach (var auditEvent in events)
            {
                auditEvent.RecordCode = receipt.Code;
            }

            try
            {
                await unitOfWork.SaveChangesAsync(cancellationToken);
                return;
            }
            catch (DuplicateRecordException) when (attempt < MaxCodeAttempts)
            {
            }
        }
    }

    /// <summary>The fields and lines of the request; lines are numbered 1…n in the order entered (GBR-04).</summary>
    private void Apply(PurchaseReceipt receipt, SavePurchaseReceiptRequest request, ReceiptLineRefs refs)
    {
        receipt.SupplierId = request.SupplierId!.Value;
        receipt.ReceiptDate = request.ReceiptDate ?? timeProvider.Today();
        receipt.Notes = Trimmed(request.Notes);
        var lineNumber = 0;
        foreach (var input in request.Lines ?? [])
        {
            var item = input.ItemId is { } itemId ? refs.Items[itemId] : null;
            var line = new PurchaseReceiptLine
            {
                ReceiptId = receipt.Id,
                LineNumber = ++lineNumber,
                ItemId = item?.Id,
                Unit = item?.Unit ?? string.Empty,
                Quantity = input.Quantity,
                UnitPrice = input.UnitPrice,
                LineAmount = PurchaseMoney.LineAmount(input.Quantity, input.UnitPrice),
                SupplierLotCode = item?.Group == InputItemGroup.Seed ? Trimmed(input.SupplierLotCode) : null,
                RaftId = input.RaftId,
            };
            receipt.Lines.Add(line);
            receipts.AddLine(line);
        }
    }

    private static string? Trimmed(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    public static object Snapshot(PurchaseReceipt r) => new
    {
        r.SupplierId, r.ReceiptDate, r.Notes,
        Lines = r.Lines.OrderBy(l => l.LineNumber)
            .Select(l => new { l.LineNumber, l.ItemId, l.Quantity, l.UnitPrice, l.LineAmount, l.SupplierLotCode, l.RaftId }).ToList(),
    };
}
