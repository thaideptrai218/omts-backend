using OMTS.Application.Common;
using OMTS.Application.Common.Concurrency;
using OMTS.Application.Common.Exceptions;
using OMTS.Application.Common.Interfaces;
using OMTS.Application.Common.Interfaces.Persistence;
using OMTS.Application.Common.Interfaces.Storage;
using OMTS.Application.Common.Messages;
using OMTS.Application.Common.Results;
using OMTS.Application.Features.Audit;
using OMTS.Application.Features.Audit.Models;
using OMTS.Application.Features.Authorization;
using OMTS.Application.Features.Authorization.Models;
using OMTS.Application.Features.Files;
using OMTS.Domain.Constants;
using OMTS.Domain.Entities.Cultivation;
using OMTS.Domain.Entities.Platform;
using OMTS.Domain.Entities.Purchasing;
using OMTS.Domain.Enums;

namespace OMTS.Application.Features.Purchasing.Receipts;

/// <summary>
/// INP-03 – purchase receipts: saved as Draft or Completed. Completing creates one seed lot per seed line and
/// the paid-now payment in the same save. A Completed receipt is never edited (cancel and copy, INP-04).
/// </summary>
public sealed class PurchaseReceiptService(
    IPurchaseReceiptRepository receipts,
    ISupplierPaymentRepository payments,
    IFarmingInputItemRepository items,
    PurchaseReceiptValidator validator,
    PurchaseReceiptViews views,
    ZoneAccessGuard zoneAccess,
    ICurrentUser currentUser,
    IFileStorage storage,
    IStatusCatalog statusCatalog,
    RowVersionGuard rowVersions,
    AuditLogService audit,
    IUnitOfWork unitOfWork,
    TimeProvider timeProvider)
{
    private const string RecordType = nameof(PurchaseReceipt);
    private const int MaxCodeAttempts = 5;

    public async Task<Result<PurchaseReceiptView>> GetAsync(Guid receiptId, CancellationToken cancellationToken)
    {
        var receipt = await receipts.GetForEditAsync(receiptId, cancellationToken);
        if (receipt is null)
        {
            return AppError.NotFound;
        }

        return await zoneAccess.AllowsAsync(PermissionFunctions.PurchaseReceipts, PermissionAction.View, receipt.ZoneId, cancellationToken)
            ? await views.ViewAsync(receipt.Id, cancellationToken)
            : AppError.Forbidden;
    }

    public async Task<Result<PurchaseReceiptView>> CreateAsync(SavePurchaseReceiptRequest request, CancellationToken cancellationToken)
    {
        if (request.ZoneId is { } zoneId && !await zoneAccess.AllowsAsync(PermissionFunctions.PurchaseReceipts, PermissionAction.Create, zoneId, cancellationToken))
        {
            return AppError.Forbidden;
        }

        var (lineItems, invalid) = await validator.CheckDraftAsync(request, cancellationToken);
        if (invalid is not null)
        {
            return invalid;
        }

        var prefix = DocumentCodes.PrefixFor(DocumentCodes.ReceiptPrefix, timeProvider.Today());
        var used = await receipts.CountCodesAsync(prefix, cancellationToken);
        var receipt = new PurchaseReceipt { RecordedByAccountId = currentUser.AccountId!.Value, Code = DocumentCodes.Next(prefix, used) };
        Apply(receipt, request, lineItems);
        var events = new List<AuditEvent>();
        if (request.Complete && await CompleteAsync(receipt, lineItems, cancellationToken, events) is { } error)
        {
            return error;
        }

        receipts.Add(receipt);
        events.Add(audit.Record(new AuditEntry("PurchaseReceipt.Created", RecordType)
        {
            RecordId = receipt.Id, RecordCode = receipt.Code, ZoneId = receipt.ZoneId, After = Snapshot(receipt),
        }));
        await SaveWithCodeAsync(receipt, prefix, used, events, cancellationToken);
        return await views.ViewAsync(receipt.Id, cancellationToken);
    }

    /// <summary>Edits a Draft (all fields and lines); with <c>complete</c> it is completed in the same save.</summary>
    public async Task<Result<PurchaseReceiptView>> UpdateAsync(Guid receiptId, SavePurchaseReceiptRequest request, CancellationToken cancellationToken)
    {
        var receipt = await receipts.GetForEditAsync(receiptId, cancellationToken);
        if (receipt is null)
        {
            return AppError.NotFound;
        }

        if (await EditableDraftAsync(receipt, request.ZoneId, request.RowVersion, cancellationToken, requireRowVersion: true) is { } refused)
        {
            return refused;
        }

        var (lineItems, invalid) = await validator.CheckDraftAsync(request, cancellationToken);
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

        Apply(receipt, request, lineItems);
        if (request.Complete && await CompleteAsync(receipt, lineItems, cancellationToken) is { } error)
        {
            return error;
        }

        var (changedBefore, changedAfter) = AuditDiff.Changed(before, Snapshot(receipt));
        if (changedAfter is not null)
        {
            audit.Record(new AuditEntry("PurchaseReceipt.Updated", RecordType)
            {
                RecordId = receipt.Id, RecordCode = receipt.Code, ZoneId = receipt.ZoneId, Before = changedBefore, After = changedAfter,
            });
        }

        await unitOfWork.SaveChangesAsync(cancellationToken);
        return await views.ViewAsync(receipt.Id, cancellationToken);
    }

    public async Task<Result<PurchaseReceiptView>> CompleteAsync(Guid receiptId, CompleteReceiptRequest request, CancellationToken cancellationToken)
    {
        var receipt = await receipts.GetForEditAsync(receiptId, cancellationToken);
        if (receipt is null)
        {
            return AppError.NotFound;
        }

        if (await EditableDraftAsync(receipt, null, request.RowVersion, cancellationToken, toStatusId: StatusIds.PurchaseReceipt.Completed) is { } refused)
        {
            return refused;
        }

        var lineItems = (await items.GetByIdsAsync(receipt.Lines.Select(l => l.ItemId).Distinct().ToList(), cancellationToken)).ToDictionary(i => i.Id);
        if (await CompleteAsync(receipt, lineItems, cancellationToken) is { } error)
        {
            return error;
        }

        await unitOfWork.SaveChangesAsync(cancellationToken);
        return await views.ViewAsync(receipt.Id, cancellationToken);
    }

    /// <summary>Invoice photos (JPEG, PNG or PDF, 10 MB, 10 per save) added to a Draft receipt the caller may edit (MSG61 otherwise).</summary>
    public async Task<Result<PurchaseReceiptView>> AddInvoicePhotosAsync(Guid receiptId, IReadOnlyList<IncomingFile> files, CancellationToken cancellationToken)
    {
        var receipt = await receipts.GetForEditAsync(receiptId, cancellationToken);
        if (receipt is null)
        {
            return AppError.NotFound;
        }

        if (!await zoneAccess.AllowsAsync(PermissionFunctions.PurchaseReceipts, PermissionAction.Edit, receipt.ZoneId, cancellationToken))
        {
            return AppError.Forbidden;
        }

        // A Completed receipt is never edited, and its seed lots copied the photos at completion.
        if (receipt.StatusId != StatusIds.PurchaseReceipt.Draft)
        {
            return AppError.Conflict(UserMessages.CannotChangeStatus(
                "Purchase receipt", statusCatalog.DisplayNameOf(receipt.StatusId), statusCatalog.DisplayNameOf(receipt.StatusId),
                "invoice photos can only be added to a Draft receipt"));
        }

        var validated = FileValidator.Validate(files, FileRules.Documents);
        if (files.Count == 0 || !validated.IsValid || receipt.InvoicePhotoUrls.Count + validated.Files.Count > FileRules.Documents.MaxFiles)
        {
            return AppError.Invalid(validated.InvalidFileName ?? "Invoice photos", validated.Problem);
        }

        var keys = new List<string>();
        foreach (var file in validated.Files)
        {
            var uploaded = await storage.UploadAsync(file, "purchase-invoices", cancellationToken);
            if (!uploaded.Succeeded)
            {
                return new AppError(ErrorKind.ServiceUnavailable, UserMessages.ServiceNotResponding("Cloud Storage"));
            }

            keys.Add(uploaded.Key!);
        }

        audit.Record(new AuditEntry("PurchaseReceipt.Updated", RecordType)
        {
            RecordId = receipt.Id, RecordCode = receipt.Code, ZoneId = receipt.ZoneId,
            Before = new { InvoicePhotos = receipt.InvoicePhotoUrls }, After = new { InvoicePhotos = receipt.InvoicePhotoUrls.Concat(keys).ToList() },
        });
        receipt.InvoicePhotoUrls = [.. receipt.InvoicePhotoUrls, .. keys];
        await unitOfWork.SaveChangesAsync(cancellationToken);
        return await views.ViewAsync(receipt.Id, cancellationToken);
    }

    private async Task<AppError?> EditableDraftAsync(
        PurchaseReceipt receipt, Guid? newZoneId, string? rowVersion, CancellationToken cancellationToken, int toStatusId = StatusIds.PurchaseReceipt.Draft, bool requireRowVersion = false)
    {
        foreach (var zoneId in new[] { receipt.ZoneId, newZoneId ?? receipt.ZoneId }.Distinct())
        {
            if (!await zoneAccess.AllowsAsync(PermissionFunctions.PurchaseReceipts, PermissionAction.Edit, zoneId, cancellationToken))
            {
                return AppError.Forbidden;
            }
        }

        if (receipt.StatusId != StatusIds.PurchaseReceipt.Draft)
        {
            return AppError.Conflict(UserMessages.CannotChangeStatus(
                "Purchase receipt", statusCatalog.DisplayNameOf(receipt.StatusId),
                statusCatalog.DisplayNameOf(toStatusId == StatusIds.PurchaseReceipt.Draft ? receipt.StatusId : toStatusId),
                "only a Draft receipt can be edited or completed"));
        }

        // A draft edit replaces every line, so it must say which version it read (MSG01 when missing); complete is guarded by status.
        return rowVersion is null && !requireRowVersion ? null : await rowVersions.CheckAsync(receipt, rowVersion, cancellationToken);
    }

    /// <summary>Draft → Completed: full validation, seed lots for seed lines, the paid-now payment.</summary>
    private async Task<AppError?> CompleteAsync(
        PurchaseReceipt receipt, IReadOnlyDictionary<Guid, FarmingInputItem> lineItems, CancellationToken cancellationToken, List<AuditEvent>? events = null)
    {
        if (await validator.CheckCompleteAsync(receipt, lineItems, cancellationToken) is { } invalid)
        {
            return invalid;
        }

        var now = timeProvider.GetUtcNow();
        receipt.StatusId = StatusIds.PurchaseReceipt.Completed;
        receipt.CompletedAt = now;
        var seedLines = receipt.Lines.Where(l => lineItems[l.ItemId].Group == InputItemGroup.Seed).ToList();
        for (var i = 0; i < seedLines.Count; i++)
        {
            var line = seedLines[i];
            line.SeedLot = new SeedReceiptLot
            {
                InternalCode = LotCode(receipt.Code, i),
                SeedTypeId = line.ItemId,
                SupplierId = receipt.SupplierId,
                PurchaseReceiptLineId = line.Id,
                Quantity = line.Quantity,
                Unit = line.Unit,
                ReceivedAt = new DateTimeOffset(receipt.ReceiptDate.ToDateTime(TimeOnly.MinValue), BusinessClock.UtcOffset),
                SupplierLotCode = line.SupplierLotCode,
                ActualSource = line.ActualSource,
                EvidenceUrls = [.. receipt.InvoicePhotoUrls],
            };
            receipts.AddSeedLot(line.SeedLot);
        }

        if (receipt.AmountPaidNow > 0)
        {
            var payment = new SupplierPayment
            {
                SupplierId = receipt.SupplierId,
                ReceiptId = receipt.Id,
                Amount = receipt.AmountPaidNow,
                PaymentDate = receipt.ReceiptDate,
                Method = receipt.PaidNowMethod!.Value,
                RecordedByAccountId = currentUser.AccountId!.Value,
            };
            receipt.Payments.Add(payment);
            payments.Add(payment);
            audit.Record(new AuditEntry("SupplierPayment.Recorded", nameof(SupplierPayment))
            {
                RecordId = payment.Id, ZoneId = receipt.ZoneId, After = new { payment.Amount, payment.Method, payment.PaymentDate, ReceiptId = receipt.Id },
            });
        }

        var changed = audit.RecordStatusChange(RecordType, receipt.Id, receipt.Code, StatusIds.PurchaseReceipt.Draft, StatusIds.PurchaseReceipt.Completed, zoneId: receipt.ZoneId);
        events?.Add(changed);
        return null;
    }

    /// <summary>
    /// Saves a new receipt; when a parallel save took the same daily code, the next number is used for the
    /// receipt, its seed lots and its audit rows.
    /// </summary>
    private async Task SaveWithCodeAsync(PurchaseReceipt receipt, string prefix, int used, List<AuditEvent> events, CancellationToken cancellationToken)
    {
        for (var attempt = 0; ; attempt++)
        {
            receipt.Code = DocumentCodes.Next(prefix, used, attempt);
            var lots = receipt.Lines.Select(l => l.SeedLot).OfType<SeedReceiptLot>().ToList();
            for (var i = 0; i < lots.Count; i++)
            {
                lots[i].InternalCode = LotCode(receipt.Code, i);
            }

            events.ForEach(e => e.RecordCode = receipt.Code);
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

    private static string LotCode(string receiptCode, int index) => $"{receiptCode}-S{index + 1}";

    private void Apply(PurchaseReceipt receipt, SavePurchaseReceiptRequest request, IReadOnlyDictionary<Guid, FarmingInputItem> lineItems)
    {
        receipt.SupplierId = request.SupplierId!.Value;
        receipt.ZoneId = request.ZoneId!.Value;
        receipt.RaftId = request.RaftId;
        // A Draft may have no date yet (stored as DateOnly.MinValue, shown as null); completion requires one.
        receipt.ReceiptDate = request.ReceiptDate ?? default;
        receipt.DiscountAmount = request.DiscountAmount ?? 0;
        receipt.AmountPaidNow = request.AmountPaidNow ?? 0;
        receipt.PaidNowMethod = request.PaidNowMethod;
        receipt.Notes = string.IsNullOrWhiteSpace(request.Notes) ? null : request.Notes.Trim();
        foreach (var line in request.Lines ?? [])
        {
            var item = lineItems[line.ItemId!.Value];
            var (quantity, unitPrice) = (line.Quantity ?? 0, line.UnitPrice ?? 0);
            var added = new PurchaseReceiptLine
            {
                ReceiptId = receipt.Id,
                ItemId = item.Id,
                Unit = item.Unit,
                Quantity = quantity,
                UnitPrice = unitPrice,
                LineAmount = PurchaseMoney.LineAmount(quantity, unitPrice),
                SupplierLotCode = item.Group == InputItemGroup.Seed ? Trimmed(line.SupplierLotCode) : null,
                ActualSource = item.Group == InputItemGroup.Seed ? Trimmed(line.ActualSource) : null,
            };
            receipt.Lines.Add(added);
            receipts.AddLine(added);
        }
    }

    private static string? Trimmed(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static object Snapshot(PurchaseReceipt r) => new
    {
        r.SupplierId, r.ZoneId, r.RaftId, r.ReceiptDate, r.DiscountAmount, r.AmountPaidNow, r.PaidNowMethod, r.Notes,
        Lines = r.Lines.Select(l => new { l.ItemId, l.Quantity, l.UnitPrice, l.LineAmount, l.SupplierLotCode, l.ActualSource }).ToList(),
    };
}
