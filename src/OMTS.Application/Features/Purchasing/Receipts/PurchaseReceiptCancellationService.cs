using OMTS.Application.Features.Purchasing.Receipts.Dtos;
using OMTS.Application.Common;
using OMTS.Application.Common.Concurrency;
using OMTS.Application.Common.Exceptions;
using OMTS.Application.Common.Interfaces;
using OMTS.Application.Common.Interfaces.Persistence;
using OMTS.Application.Common.Messages;
using OMTS.Application.Common.Results;
using OMTS.Application.Features.Audit;
using OMTS.Application.Features.Audit.Models;
using OMTS.Application.Features.Authorization;
using OMTS.Application.Features.Authorization.Models;
using OMTS.Domain.Constants;
using OMTS.Domain.Entities.Purchasing;

namespace OMTS.Application.Features.Purchasing.Receipts;

/// <summary>
/// INP-04 – cancel a wrong receipt with a reason (its payments cancelled or kept as an unallocated supplier
/// amount) and copy a Completed or Cancelled receipt into a new Draft. Correction = cancel + copy.
/// </summary>
public sealed class PurchaseReceiptCancellationService(
    IPurchaseReceiptRepository receipts,
    PurchaseReceiptViews views,
    ZoneAccessGuard zoneAccess,
    ICurrentUser currentUser,
    IStatusCatalog statusCatalog,
    RowVersionGuard rowVersions,
    AuditLogService audit,
    IUnitOfWork unitOfWork,
    TimeProvider timeProvider)
{
    private const string RecordType = nameof(PurchaseReceipt);

    public async Task<Result<PurchaseReceiptView>> CancelAsync(Guid receiptId, CancelReceiptRequest request, CancellationToken cancellationToken)
    {
        var receipt = await receipts.GetForEditAsync(receiptId, cancellationToken);
        if (receipt is null)
        {
            return AppError.NotFound;
        }

        if (!await zoneAccess.AllowsAsync(PermissionFunctions.PurchaseReceipts, PermissionAction.Delete, receipt.ZoneId, cancellationToken))
        {
            return AppError.Forbidden;
        }

        var reason = request.Reason?.Trim() ?? string.Empty;
        if (reason.Length is 0 or > 1000)
        {
            return AppError.Invalid("Reason");
        }

        if (await BlockedAsync(receipt, cancellationToken) is { } blocked)
        {
            return blocked;
        }

        var recorded = receipt.Payments.Where(p => p.StatusId == StatusIds.SupplierPayment.Recorded).ToList();
        if (recorded.Count > 0 && request.CancelPayments is null)
        {
            return AppError.Invalid("Cancel payments");
        }

        if (request.RowVersion is not null && await rowVersions.CheckAsync(receipt, request.RowVersion, cancellationToken) is { } conflict)
        {
            return conflict;
        }

        var oldStatusId = receipt.StatusId;
        receipt.StatusId = StatusIds.PurchaseReceipt.Cancelled;
        receipt.CancelledAt = timeProvider.GetUtcNow();
        receipt.CancelReason = reason;
        foreach (var payment in recorded)
        {
            if (request.CancelPayments == true)
            {
                payment.StatusId = StatusIds.SupplierPayment.Cancelled;
                payment.CancelReason = reason;
                audit.RecordStatusChange(nameof(SupplierPayment), payment.Id, null, StatusIds.SupplierPayment.Recorded, StatusIds.SupplierPayment.Cancelled, reason, receipt.ZoneId);
            }
            else
            {
                // Kept as money the supplier holds; the Accountant allocates it to another receipt (INP-06).
                receipt.Payments.Remove(payment);
                payment.ReceiptId = null;
                audit.Record(new AuditEntry("SupplierPayment.Detached", nameof(SupplierPayment))
                {
                    RecordId = payment.Id, ZoneId = receipt.ZoneId, Before = new { ReceiptId = receipt.Id }, After = new { ReceiptId = (Guid?)null }, Reason = reason,
                });
            }
        }

        audit.Record(new AuditEntry("PurchaseReceipt.Cancelled", RecordType)
        {
            RecordId = receipt.Id,
            RecordCode = receipt.Code,
            ZoneId = receipt.ZoneId,
            Before = new { Status = statusCatalog.CodeOf(oldStatusId) },
            After = new { Status = statusCatalog.CodeOf(receipt.StatusId), PaymentsCancelled = recorded.Count > 0 ? request.CancelPayments : null },
            Reason = reason,
        });
        await unitOfWork.SaveChangesAsync(cancellationToken);
        return await views.ViewAsync(receipt.Id, cancellationToken);
    }

    /// <summary>A new Draft with the same supplier, zone, raft, lines, discount, notes and photos; no payments, no seed lots.</summary>
    public async Task<Result<PurchaseReceiptView>> CopyAsync(Guid receiptId, CancellationToken cancellationToken)
    {
        var source = await receipts.GetForEditAsync(receiptId, cancellationToken);
        if (source is null)
        {
            return AppError.NotFound;
        }

        if (!await zoneAccess.AllowsAsync(PermissionFunctions.PurchaseReceipts, PermissionAction.Create, source.ZoneId, cancellationToken))
        {
            return AppError.Forbidden;
        }

        if (source.StatusId == StatusIds.PurchaseReceipt.Draft)
        {
            // A Draft is edited directly, not copied.
            return AppError.Conflict(UserMessages.WrongStatus($"Purchase receipt {source.Code}", statusCatalog.DisplayNameOf(source.StatusId)));
        }

        var prefix = DocumentCodes.PrefixFor(DocumentCodes.ReceiptPrefix, timeProvider.Today());
        var used = await receipts.CountCodesAsync(prefix, cancellationToken);
        var copy = new PurchaseReceipt
        {
            Code = DocumentCodes.Next(prefix, used),
            SupplierId = source.SupplierId,
            ZoneId = source.ZoneId,
            RaftId = source.RaftId,
            ReceiptDate = source.ReceiptDate,
            DiscountAmount = source.DiscountAmount,
            Notes = source.Notes,
            InvoicePhotoUrls = [.. source.InvoicePhotoUrls],
            CopiedFromReceiptId = source.Id,
            RecordedByAccountId = currentUser.AccountId!.Value,
        };
        foreach (var line in source.Lines)
        {
            copy.Lines.Add(new PurchaseReceiptLine
            {
                ReceiptId = copy.Id, ItemId = line.ItemId, Unit = line.Unit, Quantity = line.Quantity, UnitPrice = line.UnitPrice,
                LineAmount = line.LineAmount, SupplierLotCode = line.SupplierLotCode, ActualSource = line.ActualSource,
            });
        }

        receipts.Add(copy);
        var copied = audit.Record(new AuditEntry("PurchaseReceipt.Copied", RecordType)
        {
            RecordId = copy.Id, RecordCode = copy.Code, ZoneId = copy.ZoneId, Before = new { SourceId = source.Id, SourceCode = source.Code },
        });
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                await unitOfWork.SaveChangesAsync(cancellationToken);
                break;
            }
            catch (DuplicateRecordException) when (attempt < 5)
            {
                // A parallel save took the code: the next number for the copy and its audit row.
                copy.Code = DocumentCodes.Next(prefix, used, attempt + 1);
                copied.RecordCode = copy.Code;
            }
        }
        return await views.ViewAsync(copy.Id, cancellationToken);
    }

    /// <summary>
    /// Already Cancelled (MSG-GEN-09); or Completed with a stocked seed lot or a purchase return (MSG-PURCHASE-09,
    /// PURCHASE-06.0.E1).
    /// </summary>
    private async Task<AppError?> BlockedAsync(PurchaseReceipt receipt, CancellationToken cancellationToken)
    {
        if (receipt.StatusId == StatusIds.PurchaseReceipt.Cancelled)
        {
            return AppError.Conflict(UserMessages.WrongStatus($"Purchase receipt {receipt.Code}", statusCatalog.DisplayNameOf(receipt.StatusId)));
        }

        if (receipt.StatusId != StatusIds.PurchaseReceipt.Completed)
        {
            return null;
        }

        var stocked = await receipts.StockedAsync(receipt.Lines.Where(l => l.SeedLot is not null).Select(l => l.Id).ToList(), cancellationToken);
        var stockedLot = receipt.Lines.FirstOrDefault(l => stocked.ContainsKey(l.Id))?.SeedLot;
        var cause = stockedLot is not null ? $"seed lot {stockedLot.InternalCode} has already been stocked"
            : receipt.Returns.Count > 0 ? "the receipt has purchase returns"
            : null;
        return cause is null ? null : AppError.Conflict(UserMessages.ReceiptCannotBeCancelled(receipt.Code, cause));
    }
}
