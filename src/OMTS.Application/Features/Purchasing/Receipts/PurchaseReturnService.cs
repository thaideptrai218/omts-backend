using OMTS.Application.Common;
using OMTS.Application.Common.Concurrency;
using OMTS.Application.Common.Constants;
using OMTS.Application.Common.Interfaces;
using OMTS.Application.Common.Interfaces.Persistence;
using OMTS.Application.Common.Messages;
using OMTS.Application.Common.Results;
using OMTS.Application.Features.Audit;
using OMTS.Application.Features.Audit.Models;
using OMTS.Application.Features.Purchasing.Items;
using OMTS.Application.Features.Purchasing.Payments;
using OMTS.Domain.Constants;
using OMTS.Domain.Entities.Farming;
using OMTS.Domain.Entities.Purchasing;
using OMTS.Domain.Enums;

namespace OMTS.Application.Features.Purchasing.Receipts;

/// <summary>
/// PURCHASE-02 – goods sent back from a Completed receipt, or on their own with no receipt code, after the Owner approved it in person
/// (the Zone Manager confirms it, MSG-PURCHASE-02), and the Owner's cancellation of one recorded by mistake. A Draft holds nothing and
/// is changed only by who recorded it or the Owner. A line of a receipt never returns more than received − seeded − returned, also
/// under parallel saves: the receipt row is part of each save that records a return, and the loser answers MSG06. Access is by role
/// (BR-FARM-08); the zone is for display and filtering. A return sends no notification (BR-PURCHASE-05, BR-PURCHASE-07).
/// Only a return that records money paid back takes the supplier row into the save, as a payment does.
/// </summary>
public sealed class PurchaseReturnService(
    IPurchaseReceiptRepository receipts,
    ISupplierRepository suppliers,
    ISupplierPaymentRepository payments,
    IFarmingInputItemRepository items,
    IFarmingZoneRepository zones,
    ISeedLotUsageQuery seedUsage,
    PurchaseReceiptViews views,
    PurchaseReceiptAccess access,
    PurchaseReturnQueryService queries,
    PurchaseDocumentCodeService codes,
    InputStockLedger ledger,
    ICurrentUser currentUser,
    IStatusCatalog statusCatalog,
    RowVersionGuard rowVersions,
    AuditLogService audit,
    IUnitOfWork unitOfWork,
    TimeProvider timeProvider)
{
    private const string RecordType = nameof(PurchaseReturn);

    /// <summary>What a save of a return refers to: its receipt when it has one, its supplier, its zone and the items of its lines.</summary>
    private sealed record ReturnSources(PurchaseReceipt? Receipt, Supplier Supplier, FarmingZone? Zone, IReadOnlyDictionary<Guid, FarmingInputItem> Items);

    public async Task<Result<PurchaseReceiptView>> CreateAsync(Guid receiptId, CreatePurchaseReturnRequest request, CancellationToken cancellationToken)
    {
        var receipt = await receipts.GetForEditAsync(receiptId, cancellationToken);
        if (receipt is null)
        {
            return AppError.NotFound;
        }

        if (receipt.StatusId != StatusIds.PurchaseReceipt.Completed)
        {
            return WrongReceiptStatus(receipt);
        }

        var invalid = PurchaseReturnRules.InvalidFields(request, receipt);
        if (!PurchaseReturnRules.ReturnedAtAllowed(request.ReturnedAt, timeProvider.GetUtcNow()))
        {
            invalid.Add("Returned at");
        }

        if (invalid.Count > 0)
        {
            return AppError.InvalidFields(invalid.Distinct().ToList());
        }

        var inputs = request.Lines!;
        var seeded = await seedUsage.SeededAsync(receipt.Lines.Select(l => l.Id).ToList(), cancellationToken);
        if (PurchaseReturnRules.QuantityError(receipt, inputs, seeded, PurchaseReceiptViews.ReturnedQuantities(receipt)) is { } quantityError)
        {
            return quantityError;
        }

        var lines = inputs.Select(input => LineFromReceipt(receipt, receipt.Lines.First(l => l.Id == input.ReceiptLineId), input.Quantity!.Value, input.UnitPrice)).ToList();
        var isOwner = await access.IsCooperativeOwnerAsync(cancellationToken);
        if (!isOwner && !request.OwnerApproved)
        {
            return PurchaseReturnRules.OwnerApprovalConfirmation(lines.Sum(l => l.ReturnValue), receipt.Supplier.Name);
        }

        // The typed code, or one taken once the return is valid and confirmed, so a refused request spends no number; never given twice.
        var (code, duplicate) = await codes.ForNewAsync(PurchaseDocumentKind.Return, request.Code, cancellationToken);
        if (duplicate is not null)
        {
            return duplicate;
        }

        var purchaseReturn = new PurchaseReturn
        {
            Code = code,
            ReceiptId = receipt.Id,
            SupplierId = receipt.SupplierId,
            ZoneId = receipt.ZoneId,
            Reason = request.Reason!.Trim(),
            RecordedByAccountId = currentUser.AccountId!.Value,
        };
        PurchaseReturnRules.SetReturned(purchaseReturn, request.ReturnedAt ?? timeProvider.GetUtcNow());
        lines.ForEach(purchaseReturn.Lines.Add);
        receipt.Returns.Add(purchaseReturn);
        receipts.AddReturn(purchaseReturn);
        unitOfWork.MarkChanged(receipt);
        RecordCreated(purchaseReturn, !isOwner);
        await ledger.PostReturnAsync(purchaseReturn, cancellationToken);
        await unitOfWork.SaveChangesAsync(cancellationToken);
        return await views.ViewAsync(receipt.Id, cancellationToken);
    }

    /// <summary>Saves a Draft, or records the return in the same save when <c>complete</c> is set.</summary>
    public async Task<Result<PurchaseReturnView>> CreateAsync(SavePurchaseReturnRequest request, CancellationToken cancellationToken)
    {
        var (sources, invalid) = await SourcesAsync(request, cancellationToken);
        if (invalid is not null)
        {
            return invalid;
        }

        var purchaseReturn = new PurchaseReturn { StatusId = StatusIds.PurchaseReturn.Draft, RecordedByAccountId = currentUser.AccountId!.Value };
        Apply(purchaseReturn, request, sources!);
        foreach (var line in BuildLines(request, sources!))
        {
            purchaseReturn.Lines.Add(line);
        }

        var isOwner = await access.IsCooperativeOwnerAsync(cancellationToken);
        if (request.Complete && await CompletionErrorAsync(purchaseReturn, sources!, request.OwnerApproved, isOwner, request.Refund, cancellationToken) is { } refused)
        {
            return refused;
        }

        var (code, duplicate) = await codes.ForNewAsync(PurchaseDocumentKind.Return, request.Code, cancellationToken);
        if (duplicate is not null)
        {
            return duplicate;
        }

        purchaseReturn.Code = code;
        receipts.AddReturn(purchaseReturn);
        RecordCreated(purchaseReturn, request.Complete && !isOwner);
        if (request.Complete)
        {
            await MarkRecordedAsync(purchaseReturn, sources!, request.Refund, cancellationToken);
        }

        await unitOfWork.SaveChangesAsync(cancellationToken);
        return await queries.ViewAsync(purchaseReturn.Id, cancellationToken);
    }

    /// <summary>
    /// Continues a Draft: replaces its fields and lines, and records it when <c>complete</c> is set. A Recorded return is edited by
    /// <see cref="EditRecordedAsync"/>. The row version is required.
    /// </summary>
    public async Task<Result<PurchaseReturnView>> UpdateAsync(Guid returnId, SavePurchaseReturnRequest request, CancellationToken cancellationToken)
    {
        var purchaseReturn = await receipts.GetReturnForEditAsync(returnId, cancellationToken);
        if (purchaseReturn is null)
        {
            return AppError.NotFound;
        }

        if (purchaseReturn.StatusId == StatusIds.PurchaseReturn.Recorded)
        {
            return await EditRecordedAsync(purchaseReturn, request, cancellationToken);
        }

        if (await EditableDraftAsync(purchaseReturn, request.RowVersion, requireRowVersion: true, cancellationToken) is { } refusedDraft)
        {
            return refusedDraft;
        }

        var (sources, invalid) = await SourcesAsync(request, cancellationToken);
        if (invalid is not null)
        {
            return invalid;
        }

        var (code, duplicate) = await codes.ForDraftAsync(PurchaseDocumentKind.Return, request.Code, purchaseReturn.Id, purchaseReturn.Code, cancellationToken);
        if (duplicate is not null)
        {
            return duplicate;
        }

        purchaseReturn.Code = code;
        foreach (var line in purchaseReturn.Lines.ToList())
        {
            purchaseReturn.Lines.Remove(line);
            receipts.RemoveReturnLine(line);
        }

        Apply(purchaseReturn, request, sources!);
        foreach (var line in BuildLines(request, sources!))
        {
            line.ReturnId = purchaseReturn.Id;
            purchaseReturn.Lines.Add(line);
            receipts.AddReturnLine(line);
        }

        // Lines are rows of their own: a lines-only edit must still give the return a new row version.
        unitOfWork.MarkChanged(purchaseReturn);
        if (request.Complete)
        {
            var isOwner = await access.IsCooperativeOwnerAsync(cancellationToken);
            if (await CompletionErrorAsync(purchaseReturn, sources!, request.OwnerApproved, isOwner, request.Refund, cancellationToken) is { } refused)
            {
                return refused;
            }

            await MarkRecordedAsync(purchaseReturn, sources!, request.Refund, cancellationToken);
        }

        await unitOfWork.SaveChangesAsync(cancellationToken);
        return await queries.ViewAsync(purchaseReturn.Id, cancellationToken);
    }

    /// <summary>Records a saved Draft at its stored lines and prices, at the time sent or now.</summary>
    public async Task<Result<PurchaseReturnView>> CompleteAsync(Guid returnId, CompletePurchaseReturnRequest request, CancellationToken cancellationToken)
    {
        var purchaseReturn = await receipts.GetReturnForEditAsync(returnId, cancellationToken);
        if (purchaseReturn is null)
        {
            return AppError.NotFound;
        }

        if (await EditableDraftAsync(purchaseReturn, request.RowVersion, requireRowVersion: false, cancellationToken) is { } refusedDraft)
        {
            return refusedDraft;
        }

        var now = timeProvider.GetUtcNow();
        if (!PurchaseReturnRules.ReturnedAtAllowed(request.ReturnedAt, now))
        {
            return AppError.Invalid("Returned at");
        }

        PurchaseReturnRules.SetReturned(purchaseReturn, request.ReturnedAt ?? now);

        var (sources, invalid) = await StoredSourcesAsync(purchaseReturn, cancellationToken);
        if (invalid is not null)
        {
            return invalid;
        }

        var isOwner = await access.IsCooperativeOwnerAsync(cancellationToken);
        if (await CompletionErrorAsync(purchaseReturn, sources!, request.OwnerApproved, isOwner, request.Refund, cancellationToken) is { } refused)
        {
            return refused;
        }

        await MarkRecordedAsync(purchaseReturn, sources!, request.Refund, cancellationToken);
        await unitOfWork.SaveChangesAsync(cancellationToken);
        return await queries.ViewAsync(purchaseReturn.Id, cancellationToken);
    }

    /// <summary>
    /// The edit of a Recorded return by anyone whose role may edit purchase receipts: its time, reason, discount and zone, and its
    /// lines. A return from a receipt keeps its lines and changes their quantities, each within received − seeded − what the other
    /// Recorded returns hold, and their prices (a line sent without one keeps its price); a return without a receipt may also add
    /// or remove lines. A new time moves it to its date in the debt. The receipt, the supplier and the money paid back never
    /// change here, and the net value never goes below what was paid back. The audit log keeps the return before and after.
    /// </summary>
    private async Task<Result<PurchaseReturnView>> EditRecordedAsync(PurchaseReturn purchaseReturn, SavePurchaseReturnRequest request, CancellationToken cancellationToken)
    {
        if (await rowVersions.CheckAsync(purchaseReturn, request.RowVersion, cancellationToken) is { } conflict)
        {
            return conflict;
        }

        var invalid = new List<string>();
        if (!DocumentCodes.SameOrEmpty(request.Code, purchaseReturn.Code))
        {
            invalid.Add("Code");
        }

        if (request.ReceiptId is { } receiptId && receiptId != purchaseReturn.ReceiptId)
        {
            invalid.Add("Receipt");
        }

        if (request.SupplierId is { } supplierId && supplierId != purchaseReturn.SupplierId)
        {
            invalid.Add("Supplier");
        }

        if (request.Reason?.Trim().Length is null or 0 or > PurchaseReturnRules.MaxReasonLength)
        {
            invalid.Add("Reason");
        }

        if (!PurchaseReceiptValidator.IsWholeAmount(request.Discount))
        {
            invalid.Add("Discount");
        }

        // Money paid back is recorded with the completion or as a supplier payment, never by an edit.
        if (request.Refund is not null)
        {
            invalid.Add("Refund");
        }

        if (!PurchaseReturnRules.ReturnedAtAllowed(request.ReturnedAt, timeProvider.GetUtcNow()))
        {
            invalid.Add("Returned at");
        }

        if (request.ZoneId is { } zoneId && zoneId != purchaseReturn.ZoneId
            && (await zones.GetByIdsAsync([zoneId], cancellationToken)).FirstOrDefault() is not { StatusId: StatusIds.Usage.InUse })
        {
            invalid.Add("Zone");
        }

        var inputs = request.Lines ?? [];
        if (inputs.Count == 0 || inputs.Count > PurchaseReceiptValidator.MaxLines || inputs.Any(l => l is null))
        {
            invalid.Add("Lines");
        }

        if (invalid.Count > 0)
        {
            return AppError.InvalidFields(invalid);
        }

        var before = EditSnapshot(purchaseReturn);
        var ledgerBefore = LedgerKey(purchaseReturn);
        var lineError = purchaseReturn.ReceiptId is { } linkedReceiptId
            ? await EditLinkedLinesAsync(purchaseReturn, linkedReceiptId, inputs, cancellationToken)
            : await EditStandaloneLinesAsync(purchaseReturn, inputs, cancellationToken);
        if (lineError is not null)
        {
            return lineError;
        }

        var value = purchaseReturn.Lines.Sum(l => l.ReturnValue);
        if (request.Discount > value)
        {
            return AppError.Invalid("Discount");
        }

        var refunded = (await receipts.RefundsOfReturnAsync(purchaseReturn.Id, cancellationToken))
            .Where(p => p.StatusId == StatusIds.SupplierPayment.Recorded).Sum(p => p.Amount);
        if (value - request.Discount < refunded)
        {
            return AppError.Invalid("Refund", $"The supplier has already paid back {refunded:0} VND for this return; its net value cannot be less.");
        }

        purchaseReturn.Reason = request.Reason!.Trim();
        purchaseReturn.Discount = request.Discount;
        purchaseReturn.ZoneId = request.ZoneId ?? purchaseReturn.ZoneId;
        if (request.ReturnedAt is { } returnedAt)
        {
            PurchaseReturnRules.SetReturned(purchaseReturn, returnedAt);
        }

        // The quantities of the return on the ledger follow the new lines and time: the old rows are undone, the new ones written.
        if (LedgerKey(purchaseReturn) != ledgerBefore)
        {
            await ledger.ReverseAsync(purchaseReturn.Id, afterReceiptRemoved: false, cancellationToken);
            await ledger.PostReturnAsync(purchaseReturn, cancellationToken);
        }

        unitOfWork.MarkChanged(purchaseReturn);
        var (changedBefore, changedAfter) = AuditDiff.Changed(before, EditSnapshot(purchaseReturn));
        if (changedAfter is not null)
        {
            audit.Record(new AuditEntry("PurchaseReturn.Edited", RecordType)
            {
                RecordId = purchaseReturn.Id, RecordCode = purchaseReturn.Code, Before = changedBefore, After = changedAfter,
            });
        }

        await unitOfWork.SaveChangesAsync(cancellationToken);
        return await queries.ViewAsync(purchaseReturn.Id, cancellationToken);
    }

    /// <summary>The same receipt lines with new quantities and prices, within what the line has left besides this return (MSG-PURCHASE-03).</summary>
    private async Task<AppError?> EditLinkedLinesAsync(
        PurchaseReturn purchaseReturn, Guid receiptId, IReadOnlyList<PurchaseReturnLineInput> inputs, CancellationToken cancellationToken)
    {
        var stored = purchaseReturn.Lines.Where(l => l.ReceiptLineId != null).ToDictionary(l => l.ReceiptLineId!.Value);
        var invalid = new List<string>();
        for (var i = 0; i < inputs.Count; i++)
        {
            if (inputs[i].ReceiptLineId is not { } lineId || !stored.ContainsKey(lineId)
                || inputs[i].Quantity is not { } quantity || quantity <= 0 || quantity > PurchaseReceiptValidator.MaxQuantity
                || !PurchaseReturnRules.PriceAllowed(inputs[i].UnitPrice, quantity))
            {
                invalid.Add($"Line {i + 1}");
            }
        }

        if (inputs.Select(l => l.ReceiptLineId).Distinct().Count() != inputs.Count || inputs.Count != stored.Count)
        {
            invalid.Add("Lines");
        }

        if (invalid.Count > 0)
        {
            return AppError.InvalidFields(invalid.Distinct().ToList());
        }

        var receipt = await receipts.GetForEditAsync(receiptId, cancellationToken);
        if (receipt is not { StatusId: StatusIds.PurchaseReceipt.Completed })
        {
            return receipt is null ? AppError.Invalid("Receipt") : WrongReceiptStatus(receipt);
        }

        var seeded = await seedUsage.SeededAsync(receipt.Lines.Select(l => l.Id).ToList(), cancellationToken);
        var others = PurchaseReceiptViews.ReturnedQuantities(receipt)
            .ToDictionary(x => x.Key, x => x.Value - (stored.TryGetValue(x.Key, out var own) ? own.Quantity : 0));
        var checkedLines = inputs.Select(l => new ReturnLineInput(l.ReceiptLineId, l.Quantity)).ToList();
        if (PurchaseReturnRules.QuantityError(receipt, checkedLines, seeded, others) is { } quantityError)
        {
            return quantityError;
        }

        foreach (var input in inputs)
        {
            var line = stored[input.ReceiptLineId!.Value];
            // Left out, the price stays the one the return was recorded with: a correction of the receipt does not change it (PM 03/11).
            var price = input.UnitPrice ?? line.EnteredUnitPrice;
            if (line.EnteredQuantity != input.Quantity!.Value || line.EnteredUnitPrice != price)
            {
                ReceiptLineUnits.SetReturnValues(line, input.Quantity.Value, price);
            }
        }

        // The receipt row is part of the save, so a parallel return of the same lines answers MSG06.
        unitOfWork.MarkChanged(receipt);
        return null;
    }

    /// <summary>New lines for a return without a receipt: an item kept from the return may be Discontinued since, a new one may not.</summary>
    private async Task<AppError?> EditStandaloneLinesAsync(PurchaseReturn purchaseReturn, IReadOnlyList<PurchaseReturnLineInput> inputs, CancellationToken cancellationToken)
    {
        var keptItems = purchaseReturn.Lines.Select(l => l.ItemId).ToHashSet();
        var found = (await items.GetByIdsAsync(inputs.Select(l => l.ItemId).OfType<Guid>().Distinct().ToList(), cancellationToken)).ToDictionary(i => i.Id);
        var invalid = new List<string>();
        for (var i = 0; i < inputs.Count; i++)
        {
            var input = inputs[i];
            var usable = input.ItemId is { } itemId && found.TryGetValue(itemId, out var item) && (item.StatusId == StatusIds.Usage.InUse || keptItems.Contains(itemId))
                && (input.ItemUnitId is null || ReceiptLineUnits.Resolve(item, input.ItemUnitId) is not null);
            if (!usable || input.ReceiptLineId is not null
                || input.Quantity is not { } quantity || quantity <= 0 || quantity > PurchaseReceiptValidator.MaxQuantity || !PurchaseReceiptValidator.HasAllowedDecimals(quantity)
                || input.UnitPrice is not { } price || !PurchaseReceiptValidator.IsWholeAmount(price) || quantity * price > PurchaseReceiptValidator.MaxAmount)
            {
                invalid.Add($"Line {i + 1}");
            }
        }

        if (invalid.Count > 0)
        {
            return AppError.InvalidFields(invalid);
        }

        foreach (var line in purchaseReturn.Lines.ToList())
        {
            purchaseReturn.Lines.Remove(line);
            receipts.RemoveReturnLine(line);
        }

        foreach (var input in inputs)
        {
            var item = found[input.ItemId!.Value];
            var line = StandaloneLine(item, input);
            line.ReturnId = purchaseReturn.Id;
            purchaseReturn.Lines.Add(line);
            receipts.AddReturnLine(line);
        }

        return null;
    }

    /// <summary>What the ledger rows of a return depend on: when it happened and, per line, the item, the base quantity and the price.</summary>
    private static string LedgerKey(PurchaseReturn r) => string.Join(
        ";", r.Lines.OrderBy(l => l.ReceiptLineId).ThenBy(l => l.ItemId).Select(l => $"{l.ItemId}|{l.Quantity}|{l.EnteredUnitPrice}|{l.UnitFactor}")) + $"@{r.ReturnedAt:O}";

    private static object EditSnapshot(PurchaseReturn r) => new
    {
        r.ReturnDate, r.ReturnedAt, r.ZoneId, r.Reason, r.Discount,
        Lines = r.Lines.OrderBy(l => l.ReceiptLineId).ThenBy(l => l.ItemId)
            .Select(l => new { l.ReceiptLineId, l.ItemId, l.ItemUnitId, Quantity = l.EnteredQuantity, UnitPrice = l.EnteredUnitPrice, l.ReturnValue }).ToList(),
        ReturnValue = r.Lines.Sum(l => l.ReturnValue),
        NetValue = r.Lines.Sum(l => l.ReturnValue) - r.Discount,
    };

    /// <summary>Deletes a Draft and its lines; the removal stays in the audit log only.</summary>
    public async Task<Result<Done>> RemoveAsync(Guid returnId, CancellationToken cancellationToken)
    {
        var purchaseReturn = await receipts.GetReturnForEditAsync(returnId, cancellationToken);
        if (purchaseReturn is null)
        {
            return AppError.NotFound;
        }

        if (!await access.MayChangeAsync(purchaseReturn, cancellationToken))
        {
            return AppError.Forbidden;
        }

        if (purchaseReturn.StatusId != StatusIds.PurchaseReturn.Draft)
        {
            return WrongStatus(purchaseReturn);
        }

        var before = Snapshot(purchaseReturn);
        foreach (var line in purchaseReturn.Lines.ToList())
        {
            purchaseReturn.Lines.Remove(line);
            receipts.RemoveReturnLine(line);
        }

        receipts.RemoveReturn(purchaseReturn);
        audit.Record(new AuditEntry("PurchaseReturn.Removed", RecordType) { RecordId = purchaseReturn.Id, RecordCode = purchaseReturn.Code, Before = before });
        await unitOfWork.SaveChangesAsync(cancellationToken);
        return Done.Value;
    }

    /// <summary>
    /// PURCHASE-02.1 (the API lets the Owner only): Recorded → Cancelled with a reason. It frees its quantities and no longer
    /// counts in the supplier debt, also in past periods, and no longer blocks the receipt's cancel.
    /// Money already paid back stays recorded, as payments stay when a receipt is cancelled.
    /// </summary>
    public async Task<Result<CancelledPurchaseReturn>> CancelAsync(Guid returnId, CancelPurchaseReturnRequest request, CancellationToken cancellationToken)
    {
        var purchaseReturn = await receipts.GetReturnForEditAsync(returnId, cancellationToken);
        if (purchaseReturn is null)
        {
            return AppError.NotFound;
        }

        var receipt = purchaseReturn.ReceiptId is { } receiptId ? await receipts.GetForEditAsync(receiptId, cancellationToken) : null;
        if (purchaseReturn.StatusId != StatusIds.PurchaseReturn.Recorded)
        {
            return WrongStatus(purchaseReturn);
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
        await ledger.ReverseAsync(purchaseReturn.Id, afterReceiptRemoved: false, cancellationToken);
        if (receipt is not null)
        {
            unitOfWork.MarkChanged(receipt);
        }

        audit.Record(new AuditEntry("PurchaseReturn.Cancelled", RecordType)
        {
            RecordId = purchaseReturn.Id,
            RecordCode = purchaseReturn.Code,
            Before = new { Status = statusCatalog.CodeOf(StatusIds.PurchaseReturn.Recorded) },
            After = new { Status = statusCatalog.CodeOf(purchaseReturn.StatusId) },
            Reason = reason,
        });
        await unitOfWork.SaveChangesAsync(cancellationToken);
        return receipt is not null
            ? new CancelledPurchaseReturn(await views.ViewAsync(receipt.Id, cancellationToken), null)
            : new CancelledPurchaseReturn(null, await queries.ViewAsync(purchaseReturn.Id, cancellationToken));
    }

    private async Task<(ReturnSources? Sources, AppError? Error)> SourcesAsync(SavePurchaseReturnRequest request, CancellationToken cancellationToken)
    {
        var invalid = new List<string>();
        PurchaseReceipt? receipt = null;
        if (request.ReceiptId is { } receiptId)
        {
            receipt = await receipts.GetForEditAsync(receiptId, cancellationToken);
            if (receipt is null)
            {
                return (null, AppError.Invalid("Receipt"));
            }

            if (receipt.StatusId != StatusIds.PurchaseReceipt.Completed)
            {
                return (null, WrongReceiptStatus(receipt));
            }
        }

        var supplier = receipt?.Supplier;
        if (receipt is null)
        {
            // A return with no receipt is a new dealing: a Discontinued supplier cannot be chosen (GBR-01).
            var chosen = request.SupplierId is { } supplierId ? await suppliers.GetForEditAsync(supplierId, cancellationToken) : null;
            if (chosen is { StatusId: StatusIds.Usage.InUse })
            {
                supplier = chosen;
            }
            else
            {
                invalid.Add("Supplier");
            }
        }
        else if (request.SupplierId is { } other && other != receipt.SupplierId)
        {
            invalid.Add("Supplier");
        }

        FarmingZone? zone = null;
        if (request.ZoneId is { } zoneId)
        {
            zone = (await zones.GetByIdsAsync([zoneId], cancellationToken)).FirstOrDefault();
            if (zone is not { StatusId: StatusIds.Usage.InUse })
            {
                invalid.Add("Zone");
            }
        }

        var lines = request.Lines ?? [];
        var itemIds = lines.Select(l => l?.ItemId).OfType<Guid>().Distinct().ToList();
        var found = (await items.GetByIdsAsync(itemIds, cancellationToken)).ToDictionary(i => i.Id);
        invalid.AddRange(PurchaseReturnRules.InvalidSavedFields(request, receipt, found));
        if (!PurchaseReturnRules.ReturnedAtAllowed(request.ReturnedAt, timeProvider.GetUtcNow()))
        {
            invalid.Add("Returned at");
        }
        return invalid.Count > 0
            ? (null, AppError.InvalidFields(invalid.Distinct().ToList()))
            : (new ReturnSources(receipt, supplier!, zone, found), null);
    }

    /// <summary>The same, for a Draft being completed as stored: it may have gone stale since it was saved.</summary>
    private async Task<(ReturnSources? Sources, AppError? Error)> StoredSourcesAsync(PurchaseReturn purchaseReturn, CancellationToken cancellationToken)
    {
        var receipt = purchaseReturn.ReceiptId is { } receiptId ? await receipts.GetForEditAsync(receiptId, cancellationToken) : null;
        if (receipt is { StatusId: not StatusIds.PurchaseReceipt.Completed })
        {
            return (null, WrongReceiptStatus(receipt));
        }

        var invalid = new List<string>();
        var supplier = receipt?.Supplier ?? await suppliers.GetForEditAsync(purchaseReturn.SupplierId, cancellationToken);
        if (receipt is null && supplier is not { StatusId: StatusIds.Usage.InUse })
        {
            invalid.Add("Supplier");
        }

        var zone = purchaseReturn.ZoneId is { } zoneId ? (await zones.GetByIdsAsync([zoneId], cancellationToken)).FirstOrDefault() : null;
        if (purchaseReturn.ZoneId is not null && zone is not { StatusId: StatusIds.Usage.InUse })
        {
            invalid.Add("Zone");
        }

        var found = (await items.GetByIdsAsync(purchaseReturn.Lines.Select(l => l.ItemId).Distinct().ToList(), cancellationToken)).ToDictionary(i => i.Id);
        if (receipt is null)
        {
            var position = 0;
            foreach (var line in purchaseReturn.Lines)
            {
                position++;
                if (found.GetValueOrDefault(line.ItemId) is not { StatusId: StatusIds.Usage.InUse })
                {
                    invalid.Add($"Line {position}");
                }
            }
        }

        return invalid.Count > 0 ? (null, AppError.InvalidFields(invalid)) : (new ReturnSources(receipt, supplier!, zone, found), null);
    }

    private void Apply(PurchaseReturn purchaseReturn, SavePurchaseReturnRequest request, ReturnSources sources)
    {
        purchaseReturn.ReceiptId = sources.Receipt?.Id;
        purchaseReturn.SupplierId = sources.Supplier.Id;
        purchaseReturn.ZoneId = request.ZoneId ?? sources.Receipt?.ZoneId;
        purchaseReturn.Reason = request.Reason?.Trim() ?? string.Empty;
        purchaseReturn.Discount = request.Discount;
        PurchaseReturnRules.SetReturned(purchaseReturn, request.ReturnedAt ?? timeProvider.GetUtcNow());
    }

    private static List<PurchaseReturnLine> BuildLines(SavePurchaseReturnRequest request, ReturnSources sources) =>
        (request.Lines ?? []).Select(input =>
        {
            if (sources.Receipt is { } receipt)
            {
                return LineFromReceipt(receipt, receipt.Lines.First(l => l.Id == input.ReceiptLineId), input.Quantity!.Value, input.UnitPrice);
            }

            return StandaloneLine(sources.Items[input.ItemId!.Value], input);
        }).ToList();

    /// <summary>A line of a return with no receipt, in the unit of the item that was chosen (the base unit by default).</summary>
    private static PurchaseReturnLine StandaloneLine(FarmingInputItem item, PurchaseReturnLineInput input)
    {
        var unit = ReceiptLineUnits.Resolve(item, input.ItemUnitId);
        var line = new PurchaseReturnLine { ItemId = item.Id };
        ReceiptLineUnits.FillReturn(line, unit?.Unit?.Name ?? item.Unit, unit?.Id, unit?.Factor ?? 1m, input.Quantity!.Value, input.UnitPrice!.Value);
        return line;
    }

    /// <summary>
    /// BR-PURCHASE-02, PM 03/11: the line half up to the dong at the price entered or, left out, at what one unit of the receipt
    /// line was paid today (<see cref="PurchaseMoney.ReturnUnitPrice"/>: its line and receipt discounts taken off); kept as recorded.
    /// </summary>
    private static PurchaseReturnLine LineFromReceipt(PurchaseReceipt receipt, PurchaseReceiptLine line, decimal quantity, decimal? unitPrice)
    {
        var price = unitPrice ?? PurchaseMoney.ReturnPriceInLineUnit(receipt, line);
        var returned = new PurchaseReturnLine { ReceiptLineId = line.Id, ItemId = line.ItemId!.Value };
        ReceiptLineUnits.FillReturn(returned, line.Unit, line.ItemUnitId, line.UnitFactor, quantity, price);
        return returned;
    }

    /// <summary>Everything needed to record the return, on its lines as they will be saved; the Owner's approval is asked last.</summary>
    private async Task<AppError?> CompletionErrorAsync(
        PurchaseReturn purchaseReturn, ReturnSources sources, bool ownerApproved, bool isOwner, ReturnRefundInput? refund, CancellationToken cancellationToken)
    {
        // The prices are those of the lines as saved: entered, or what one unit was paid on the day the Draft was saved.
        var invalid = PurchaseReturnRules.InvalidToComplete(purchaseReturn, refund);
        if (invalid.Count > 0)
        {
            return AppError.InvalidFields(invalid);
        }

        if (sources.Receipt is { } receipt)
        {
            var seeded = await seedUsage.SeededAsync(receipt.Lines.Select(l => l.Id).ToList(), cancellationToken);
            var inputs = purchaseReturn.Lines.Select(l => new ReturnLineInput(l.ReceiptLineId, l.EnteredQuantity)).ToList();
            if (PurchaseReturnRules.QuantityError(receipt, inputs, seeded, PurchaseReceiptViews.ReturnedQuantities(receipt)) is { } quantityError)
            {
                return quantityError;
            }
        }

        return !isOwner && !ownerApproved
            ? PurchaseReturnRules.OwnerApprovalConfirmation(purchaseReturn.Lines.Sum(l => l.ReturnValue) - purchaseReturn.Discount, sources.Supplier.Name)
            : null;
    }

    /// <summary>
    /// Draft → Recorded, at the time already set on it: from then on it lowers the debt and holds its quantities. Any money paid
    /// back is saved with it, paid at the same time by the caller.
    /// </summary>
    private async Task MarkRecordedAsync(PurchaseReturn purchaseReturn, ReturnSources sources, ReturnRefundInput? refund, CancellationToken cancellationToken)
    {
        purchaseReturn.StatusId = StatusIds.PurchaseReturn.Recorded;
        audit.RecordStatusChange(RecordType, purchaseReturn.Id, purchaseReturn.Code, StatusIds.PurchaseReturn.Draft, StatusIds.PurchaseReturn.Recorded);
        await ledger.PostReturnAsync(purchaseReturn, cancellationToken);
        if (sources.Receipt is { } receipt)
        {
            // The receipt row is part of the save, so two returns that read one remaining quantity cannot both be recorded.
            unitOfWork.MarkChanged(receipt);
        }

        if (refund is not null)
        {
            var payment = new SupplierPayment
            {
                Code = await codes.NextAsync(PurchaseDocumentKind.Payment, cancellationToken),
                SupplierId = sources.Supplier.Id,
                Supplier = sources.Supplier,
                Kind = SupplierPaymentKind.PaidBack,
                Amount = refund.Amount!.Value,
                PaymentDate = purchaseReturn.ReturnDate,
                PaidAt = purchaseReturn.ReturnedAt,
                PaidByAccountId = currentUser.AccountId!.Value,
                Method = refund.Method!.Value,
                BankReference = refund.Method == PaymentMethod.BankTransfer ? SupplierPaymentRules.Trimmed(refund.BankReference) : null,
                Note = purchaseReturn.Code,
                PurchaseReturnId = purchaseReturn.Id,
                RecordedByAccountId = currentUser.AccountId!.Value,
            };
            payments.Add(payment);
            unitOfWork.MarkChanged(sources.Supplier);
            audit.Record(new AuditEntry("SupplierPayment.Recorded", nameof(SupplierPayment))
            {
                RecordId = payment.Id,
                RecordCode = payment.Code,
                After = new { payment.Kind, payment.SupplierId, payment.Amount, payment.PaymentDate, payment.Method, payment.BankReference, payment.PurchaseReturnId },
            });
        }
    }

    /// <summary>A Draft that the caller recorded (or any, for the Owner); MSG-GEN-09 for another status.</summary>
    private async Task<AppError?> EditableDraftAsync(PurchaseReturn purchaseReturn, string? rowVersion, bool requireRowVersion, CancellationToken cancellationToken)
    {
        if (!await access.MayChangeAsync(purchaseReturn, cancellationToken))
        {
            return AppError.Forbidden;
        }

        if (purchaseReturn.StatusId != StatusIds.PurchaseReturn.Draft)
        {
            return WrongStatus(purchaseReturn);
        }

        return rowVersion is null && !requireRowVersion ? null : await rowVersions.CheckAsync(purchaseReturn, rowVersion, cancellationToken);
    }

    private void RecordCreated(PurchaseReturn purchaseReturn, bool ownerApprovalConfirmed) => audit.Record(new AuditEntry("PurchaseReturn.Created", RecordType)
    {
        RecordId = purchaseReturn.Id,
        RecordCode = purchaseReturn.Code,
        After = new
        {
            purchaseReturn.ReceiptId, purchaseReturn.SupplierId, purchaseReturn.ReturnDate, purchaseReturn.ReturnedAt, purchaseReturn.Discount,
            // The Zone Manager's answer to MSG-PURCHASE-02; the Owner is not asked.
            OwnerApprovalConfirmed = ownerApprovalConfirmed,
            Lines = purchaseReturn.Lines.Select(l => new { l.ReceiptLineId, l.ItemId, l.ItemUnitId, Quantity = l.EnteredQuantity, UnitPrice = l.EnteredUnitPrice, l.ReturnValue }).ToList(),
        },
        Reason = purchaseReturn.Reason,
    });

    private static object Snapshot(PurchaseReturn r) => new
    {
        r.ReceiptId, r.SupplierId, r.ZoneId, r.Reason, r.Discount,
        Lines = r.Lines.Select(l => new { l.ReceiptLineId, l.ItemId, l.ItemUnitId, Quantity = l.EnteredQuantity, UnitPrice = l.EnteredUnitPrice, l.ReturnValue }).ToList(),
    };

    private AppError WrongStatus(PurchaseReturn purchaseReturn) =>
        AppError.Conflict(UserMessages.WrongStatus($"Purchase return {purchaseReturn.Code}", statusCatalog.DisplayNameOf(purchaseReturn.StatusId)));

    private AppError WrongReceiptStatus(PurchaseReceipt receipt) =>
        AppError.Conflict(UserMessages.WrongStatus($"Purchase receipt {receipt.Code}", statusCatalog.DisplayNameOf(receipt.StatusId)));
}


