using OMTS.Application.Common;
using OMTS.Application.Common.Concurrency;
using OMTS.Application.Common.Constants;
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
/// PURCHASE-01 – purchase receipts saved as Draft or Completed. The API checks the role; the zone of a receipt filters only. A Draft is
/// continued or completed only by the person who recorded it or the Owner (BR-PURCHASE-03). Completing makes each Seed
/// line a seed lot (GBR-04) and notifies the Owner (MSG-PURCHASE-01). An edit of a Completed receipt goes to
/// <see cref="PurchaseReceiptEditService"/>.
/// </summary>
public sealed class PurchaseReceiptService(
    IPurchaseReceiptRepository receipts,
    PurchaseReceiptValidator validator,
    PurchaseReceiptViews views,
    PurchaseReceiptAccess access,
    PurchaseReceiptNotifications notifications,
    PurchaseReceiptEditService edits,
    PurchaseDocumentCodeService codes,
    ICurrentUser currentUser,
    IStatusCatalog statusCatalog,
    RowVersionGuard rowVersions,
    AuditLogService audit,
    IUnitOfWork unitOfWork,
    TimeProvider timeProvider)
{
    private const string RecordType = nameof(PurchaseReceipt);

    public async Task<Result<PurchaseReceiptView>> GetAsync(Guid receiptId, CancellationToken cancellationToken) =>
        await receipts.GetForEditAsync(receiptId, cancellationToken) is { } receipt
            ? await views.ViewAsync(receipt.Id, cancellationToken)
            : AppError.NotFound;

    /// <summary>
    /// PURCHASE-01 / 01.2: the receipt, its lines, its audit and, when completed, the Owner's MSG-PURCHASE-01 go in one save:
    /// a failed save leaves nothing (only its PN number is spent, BR-PURCHASE-03: the code is given at the first save).
    /// </summary>
    public async Task<Result<PurchaseReceiptView>> CreateAsync(SavePurchaseReceiptRequest request, CancellationToken cancellationToken)
    {
        var (refs, invalid) = await validator.CheckDraftAsync(request, cancellationToken);
        if (invalid is not null)
        {
            return invalid;
        }

        var receipt = new PurchaseReceipt { RecordedByAccountId = currentUser.AccountId!.Value };
        Apply(receipt, request, refs, kept: null);
        if (request.Complete && await validator.CheckCompleteAsync(receipt, refs, cancellationToken) is { } error)
        {
            return error;
        }

        // The typed code, or one taken once the receipt is valid, so a refused save spends no number; never given twice (GBR-04).
        var (code, duplicate) = await codes.ForNewAsync(PurchaseDocumentKind.Receipt, request.Code, cancellationToken);
        if (duplicate is not null)
        {
            return duplicate;
        }

        receipt.Code = code;
        receipts.Add(receipt);
        audit.Record(new AuditEntry("PurchaseReceipt.Created", RecordType) { RecordId = receipt.Id, RecordCode = receipt.Code, After = Snapshot(receipt) });
        if (request.Complete)
        {
            MarkCompleted(receipt);
            await notifications.CompletedAsync(receipt, cancellationToken);
        }

        await unitOfWork.SaveChangesAsync(cancellationToken);
        return await views.ViewAsync(receipt.Id, cancellationToken);
    }

    /// <summary>
    /// Continues a Draft (PURCHASE-01.3): replaces its fields and lines; with <c>complete</c> it is completed in the same save. A
    /// Completed receipt is edited by <see cref="PurchaseReceiptEditService"/>; a Cancelled one is never changed.
    /// </summary>
    public async Task<Result<PurchaseReceiptView>> UpdateAsync(Guid receiptId, SavePurchaseReceiptRequest request, CancellationToken cancellationToken)
    {
        var receipt = await receipts.GetForEditAsync(receiptId, cancellationToken);
        if (receipt is null)
        {
            return AppError.NotFound;
        }

        if (receipt.StatusId == StatusIds.PurchaseReceipt.Completed)
        {
            return await edits.EditAsync(receipt, request, cancellationToken);
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

        var (code, duplicate) = await codes.ForDraftAsync(PurchaseDocumentKind.Receipt, request.Code, receipt.Id, receipt.Code, cancellationToken);
        if (duplicate is not null)
        {
            return duplicate;
        }

        var before = Snapshot(receipt);
        receipt.Code = code;
        foreach (var line in receipt.Lines.ToList())
        {
            receipt.Lines.Remove(line);
            receipts.RemoveLine(line);
        }

        Apply(receipt, request, refs, kept: receipt.ReceivedAt);
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
    private void MarkCompleted(PurchaseReceipt receipt)
    {
        receipt.StatusId = StatusIds.PurchaseReceipt.Completed;
        receipt.CompletedAt = timeProvider.GetUtcNow();
        audit.RecordStatusChange(RecordType, receipt.Id, receipt.Code, StatusIds.PurchaseReceipt.Draft, StatusIds.PurchaseReceipt.Completed);
    }

    /// <summary>The fields and lines of the request; lines are numbered 1…n in the order entered (GBR-04).</summary>
    private void Apply(PurchaseReceipt receipt, SavePurchaseReceiptRequest request, ReceiptLineRefs refs, DateTimeOffset? kept)
    {
        receipt.SupplierId = request.SupplierId!.Value;
        SetReceived(receipt, ReceivedAt(request, timeProvider.GetUtcNow(), kept));
        receipt.ReceivedByAccountId = request.ReceivedById ?? receipt.ReceivedByAccountId ?? currentUser.AccountId!.Value;
        receipt.Notes = Trimmed(request.Notes);
        receipt.Discount = request.Discount;
        receipt.ZoneId = request.ZoneId;
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
                Discount = input.Discount ?? 0,
                LineAmount = PurchaseMoney.LineAmount(input.Quantity, input.UnitPrice, input.Discount ?? 0),
                SupplierLotCode = item?.Group == InputItemGroup.Seed ? Trimmed(input.SupplierLotCode) : null,
                RaftId = input.RaftId,
            };
            receipt.Lines.Add(line);
            receipts.AddLine(line);
        }
    }

    internal static string? Trimmed(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    /// <summary>
    /// When the goods were received, at UTC+7: <c>receivedAt</c> when sent; otherwise the date received at the time now when it
    /// is today, or at the start of an earlier day. <paramref name="kept"/> (the stored time of a receipt being edited) stays
    /// when the request sends no time and no other day.
    /// </summary>
    public static DateTimeOffset ReceivedAt(SavePurchaseReceiptRequest request, DateTimeOffset now, DateTimeOffset? kept)
    {
        if (request.ReceivedAt is { } receivedAt)
        {
            return receivedAt.ToOffset(BusinessClock.UtcOffset);
        }

        var today = BusinessClock.ToBusinessDate(now);
        if (kept is { } stored && (request.ReceiptDate is null || request.ReceiptDate == BusinessClock.ToBusinessDate(stored)))
        {
            return stored;
        }

        var date = request.ReceiptDate ?? today;
        return date == today ? now.ToOffset(BusinessClock.UtcOffset) : BusinessClock.FromLocal(date.ToDateTime(TimeOnly.MinValue));
    }

    /// <summary>The time received and, from it, the date received that every period filter reads.</summary>
    public static void SetReceived(PurchaseReceipt receipt, DateTimeOffset receivedAt)
    {
        receipt.ReceivedAt = receivedAt;
        receipt.ReceiptDate = BusinessClock.ToBusinessDate(receivedAt);
    }

    public static object Snapshot(PurchaseReceipt r) => new
    {
        r.Code, r.SupplierId, r.ReceiptDate, r.ReceivedAt, r.ReceivedByAccountId, r.Notes, r.Discount, r.ZoneId,
        Lines = r.Lines.OrderBy(l => l.LineNumber)
            .Select(l => new { l.LineNumber, l.ItemId, l.Quantity, l.UnitPrice, l.Discount, l.LineAmount, l.SupplierLotCode, l.RaftId }).ToList(),
    };
}
