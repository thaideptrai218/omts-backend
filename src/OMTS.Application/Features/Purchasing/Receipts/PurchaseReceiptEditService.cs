using OMTS.Application.Common;
using OMTS.Application.Common.Concurrency;
using OMTS.Application.Common.Interfaces;
using OMTS.Application.Common.Interfaces.Persistence;
using OMTS.Application.Common.Messages;
using OMTS.Application.Common.Results;
using OMTS.Application.Features.Audit;
using OMTS.Application.Features.Audit.Models;
using OMTS.Domain.Constants;
using OMTS.Domain.Entities.Farming;
using OMTS.Domain.Entities.Purchasing;
using OMTS.Domain.Enums;

namespace OMTS.Application.Features.Purchasing.Receipts;

/// <summary>
/// The edit of a Completed receipt, by anyone whose role may edit purchase receipts (not only who recorded it). It may change the
/// time received and the receiver, the notes, the discount, and the lines: add, remove, or change the item, quantity, unit
/// price, discount per unit and supplier's lot code. The supplier never changes. A line that was seeded from or returned
/// (any return, Draft or Cancelled included, still names it) is never removed and keeps its item, and its quantity is never
/// set below seeded + returned (MSG-PURCHASE-10). A new Seed line is a seed lot at once, numbered after every line the
/// receipt ever had, so a seed lot code is never given twice (GBR-04). The supplier debt follows the new total; returns
/// keep the amounts they were recorded with. The audit log keeps the receipt before and after; no notification is sent.
/// </summary>
public sealed class PurchaseReceiptEditService(
    IPurchaseReceiptRepository receipts,
    ISeedLotUsageQuery seedUsage,
    PurchaseReceiptValidator validator,
    PurchaseReceiptViews views,
    PurchaseReceiptAccess access,
    RowVersionGuard rowVersions,
    AuditLogService audit,
    IUnitOfWork unitOfWork,
    TimeProvider timeProvider)
{
    /// <summary>Edits <paramref name="receipt"/>, which must be Completed; <c>complete</c> is ignored and the row version is required.</summary>
    public async Task<Result<PurchaseReceiptView>> EditAsync(PurchaseReceipt receipt, SavePurchaseReceiptRequest request, CancellationToken cancellationToken)
    {
        if (receipt.StatusId != StatusIds.PurchaseReceipt.Completed)
        {
            throw new InvalidOperationException($"Receipt {receipt.Code} is not Completed.");
        }

        if (await rowVersions.CheckAsync(receipt, request.RowVersion, cancellationToken) is { } conflict)
        {
            return conflict;
        }

        var inputs = request.Lines ?? [];
        var existing = receipt.Lines.ToDictionary(l => l.Id);
        var invalid = await InvalidHeaderAsync(receipt, request, cancellationToken);
        if (inputs.Count == 0 || inputs.Count > PurchaseReceiptValidator.MaxLines
            || inputs.Any(l => l is null || (l.LineId is { } id && !existing.ContainsKey(id)))
            || inputs.Where(l => l?.LineId is not null).Select(l => l!.LineId).Distinct().Count() != inputs.Count(l => l?.LineId is not null))
        {
            invalid.Add("Lines");
        }

        if (invalid.Count > 0)
        {
            return AppError.InvalidFields(invalid);
        }

        var refs = await validator.LoadAsync(inputs.Select(l => l.ItemId), inputs.Select(l => l.RaftId), cancellationToken);
        if (await InvalidLinesAsync(inputs, existing, refs, cancellationToken) is { Count: > 0 } invalidLines)
        {
            return AppError.InvalidFields(invalidLines);
        }

        if (inputs.Select((input, i) => (input, i)).FirstOrDefault(x => !ValuesAllowed(x.input, existing)) is { input: not null } badValues)
        {
            return PurchaseReceiptValidator.InvalidLine(badValues.i + 1);
        }

        var seeded = await seedUsage.SeededAsync(existing.Keys.ToList(), cancellationToken);
        var returned = PurchaseReceiptViews.ReturnedQuantities(receipt);
        var named = receipt.Returns.SelectMany(rt => rt.Lines).Where(l => l.ReceiptLineId != null).Select(l => l.ReceiptLineId!.Value).ToHashSet();
        if (UsedLineError(inputs, existing, seeded, returned, named) is { } usedError)
        {
            return usedError;
        }

        var receivedAt = PurchaseReceiptService.ReceivedAt(request, timeProvider.GetUtcNow(), receipt.ReceivedAt);
        if (BusinessClock.ToBusinessDate(receivedAt) is var newDate && newDate != receipt.ReceiptDate
            && await seedUsage.FirstSeedingDateAsync(seeded.Keys.ToList(), cancellationToken) is { } firstSeeding && newDate > firstSeeding)
        {
            // A seeding session is never dated before its seed lot was received.
            return AppError.Invalid("Received at", $"A seed lot of this receipt was seeded on {firstSeeding:dd/MM/yyyy}.");
        }

        var newTotal = inputs.Sum(input => PurchaseMoney.LineAmount(input.Quantity, input.UnitPrice, input.Discount ?? 0));
        if (request.Discount > newTotal)
        {
            return AppError.Invalid("Discount");
        }

        var before = Snapshot(receipt);
        PurchaseReceiptService.SetReceived(receipt, receivedAt);
        receipt.ReceivedByAccountId = request.ReceivedById ?? receipt.ReceivedByAccountId;
        receipt.Notes = PurchaseReceiptService.Trimmed(request.Notes);
        receipt.Discount = request.Discount;
        receipt.ZoneId = request.ZoneId ?? receipt.ZoneId;
        ApplyLines(receipt, inputs, existing, refs);
        // Lines are rows of their own: the receipt still gets a new row version, so a parallel edit or return answers MSG06.
        unitOfWork.MarkChanged(receipt);
        var after = Snapshot(receipt);
        var (changedBefore, changedAfter) = AuditDiff.Changed(before, after);
        if (changedAfter is not null)
        {
            audit.Record(new AuditEntry("PurchaseReceipt.Edited", nameof(PurchaseReceipt))
            {
                RecordId = receipt.Id, RecordCode = receipt.Code, Before = changedBefore, After = changedAfter,
            });
        }

        await unitOfWork.SaveChangesAsync(cancellationToken);
        return await views.ViewAsync(receipt.Id, cancellationToken);
    }

    /// <summary>The header fields: the supplier stays, a zone or receiver given must be valid, no time received later than now.</summary>
    private async Task<List<string>> InvalidHeaderAsync(PurchaseReceipt receipt, SavePurchaseReceiptRequest request, CancellationToken cancellationToken)
    {
        var invalid = new List<string>();
        // The code of a Completed receipt is printed in its seed lot codes: it never changes.
        if (!DocumentCodes.SameOrEmpty(request.Code, receipt.Code))
        {
            invalid.Add("Code");
        }

        if (request.SupplierId is { } supplierId && supplierId != receipt.SupplierId)
        {
            invalid.Add("Supplier");
        }

        if (request.ReceiptDate is { } date && date > timeProvider.Today())
        {
            invalid.Add("Receipt date");
        }

        if (request.ReceivedAt is { } receivedAt && !validator.NotInFuture(receivedAt))
        {
            invalid.Add("Received at");
        }

        if (request.ReceivedById is { } receiverId && receiverId != receipt.ReceivedByAccountId && !await validator.IsReceiverAsync(receiverId, cancellationToken))
        {
            invalid.Add("Received by");
        }

        if (request.Notes?.Trim().Length > PurchaseReceiptValidator.MaxNotesLength)
        {
            invalid.Add("Notes");
        }

        if (!PurchaseReceiptValidator.IsWholeAmount(request.Discount))
        {
            invalid.Add("Discount");
        }

        if (request.ZoneId is { } zoneId && zoneId != receipt.ZoneId && !await validator.ZoneInUseAsync(zoneId, cancellationToken))
        {
            invalid.Add("Zone");
        }

        return invalid;
    }

    /// <summary>
    /// Each line needs an item; a new or changed item must be In Use (GBR-01). A raft is checked only when it is new to the line,
    /// so an older line keeps a raft discontinued since.
    /// </summary>
    private async Task<List<string>> InvalidLinesAsync(
        IReadOnlyList<ReceiptLineInput> inputs, IReadOnlyDictionary<Guid, PurchaseReceiptLine> existing, ReceiptLineRefs refs, CancellationToken cancellationToken)
    {
        var invalid = new List<string>();
        var raftZones = await access.RaftZonesAsync(cancellationToken);
        for (var i = 0; i < inputs.Count; i++)
        {
            var input = inputs[i];
            var stored = input.LineId is { } lineId ? existing[lineId] : null;
            var item = input.ItemId is { } itemId ? refs.Items.GetValueOrDefault(itemId) : null;
            if (item is null || (item.Id != stored?.ItemId && item.StatusId != StatusIds.Usage.InUse))
            {
                invalid.Add($"Line {i + 1} item");
            }

            if (input.SupplierLotCode?.Trim().Length > PurchaseReceiptValidator.MaxSupplierLotCodeLength)
            {
                invalid.Add($"Line {i + 1} supplier lot code");
            }

            if (input.RaftId is { } raftId && raftId != stored?.RaftId && !PurchaseReceiptValidator.RaftAllowed(item, refs.Rafts.GetValueOrDefault(raftId), raftZones))
            {
                invalid.Add($"Line {i + 1} raft");
            }
        }

        return invalid;
    }

    /// <summary>
    /// The values of a complete line. A quantity stored before the 2-decimal rule and left unchanged does not stop the edit of
    /// the rest of the line.
    /// </summary>
    private static bool ValuesAllowed(ReceiptLineInput input, IReadOnlyDictionary<Guid, PurchaseReceiptLine> existing)
    {
        if (input.Quantity is not { } quantity || input.UnitPrice is null)
        {
            return false;
        }

        var keptQuantity = input.LineId is { } lineId && existing[lineId].Quantity == quantity;
        return PurchaseReceiptValidator.LineValuesAllowed(keptQuantity ? decimal.Round(quantity, 2) : quantity, input.UnitPrice, input.Discount ?? 0)
            && quantity > 0 && quantity <= PurchaseReceiptValidator.MaxQuantity;
    }

    /// <summary>What was seeded from a line or returned of it stays: the line is kept, with its item and at least that quantity.</summary>
    private static AppError? UsedLineError(
        IReadOnlyList<ReceiptLineInput> inputs,
        IReadOnlyDictionary<Guid, PurchaseReceiptLine> existing,
        IReadOnlyDictionary<Guid, decimal> seeded,
        IReadOnlyDictionary<Guid, decimal> returned,
        IReadOnlySet<Guid> namedByReturns)
    {
        bool Used(Guid lineId) => seeded.GetValueOrDefault(lineId) > 0 || namedByReturns.Contains(lineId);
        var kept = inputs.Where(l => l.LineId is not null).Select(l => l.LineId!.Value).ToHashSet();
        foreach (var removed in existing.Values.Where(l => !kept.Contains(l.Id) && Used(l.Id)).OrderBy(l => l.LineNumber))
        {
            return new AppError(
                ErrorKind.Validation,
                UserMessages.Invalid("Lines"),
                new Dictionary<string, object?>
                {
                    ["field"] = "Lines",
                    ["lineId"] = removed.Id,
                    ["hint"] = $"{removed.Item?.Name ?? $"Line {removed.LineNumber}"} was seeded from or returned, so it cannot be removed.",
                });
        }

        for (var i = 0; i < inputs.Count; i++)
        {
            if (inputs[i].LineId is not { } lineId)
            {
                continue;
            }

            var line = existing[lineId];
            if (inputs[i].ItemId != line.ItemId && Used(lineId))
            {
                return AppError.Invalid($"Line {i + 1} item", "The line was seeded from or returned, so its item cannot change.");
            }

            var (lineSeeded, lineReturned) = (seeded.GetValueOrDefault(lineId), returned.GetValueOrDefault(lineId));
            // Seeding does not compare with what is left (GBR-04), so an unchanged quantity may already be below the sum.
            if (inputs[i].Quantity != line.Quantity && inputs[i].Quantity < lineSeeded + lineReturned)
            {
                return PurchaseReceiptValidator.QuantityBelowUsed(line, $"Line {i + 1}", lineSeeded, lineReturned);
            }
        }

        return null;
    }

    /// <summary>Changes the kept lines, removes the others and adds the new ones after the highest line number the receipt had.</summary>
    private void ApplyLines(
        PurchaseReceipt receipt, IReadOnlyList<ReceiptLineInput> inputs, IReadOnlyDictionary<Guid, PurchaseReceiptLine> existing, ReceiptLineRefs refs)
    {
        var nextNumber = existing.Values.Select(l => l.LineNumber).DefaultIfEmpty(0).Max();
        var kept = inputs.Where(l => l.LineId is not null).Select(l => l.LineId!.Value).ToHashSet();
        foreach (var removed in existing.Values.Where(l => !kept.Contains(l.Id)))
        {
            receipt.Lines.Remove(removed);
            receipts.RemoveLine(removed);
        }

        foreach (var input in inputs)
        {
            var item = refs.Items[input.ItemId!.Value];
            PurchaseReceiptLine line;
            if (input.LineId is { } lineId)
            {
                line = existing[lineId];
            }
            else
            {
                line = new PurchaseReceiptLine { ReceiptId = receipt.Id, LineNumber = ++nextNumber };
                receipt.Lines.Add(line);
                receipts.AddLine(line);
            }

            Fill(line, input, item);
        }
    }

    private static void Fill(PurchaseReceiptLine line, ReceiptLineInput input, FarmingInputItem item)
    {
        if (line.ItemId != item.Id)
        {
            line.ItemId = item.Id;
            line.Item = item;
            line.Unit = item.Unit;
        }

        line.Quantity = input.Quantity;
        line.UnitPrice = input.UnitPrice;
        line.Discount = input.Discount ?? 0;
        line.LineAmount = PurchaseMoney.LineAmount(line.Quantity, line.UnitPrice, line.Discount);
        line.SupplierLotCode = item.Group == InputItemGroup.Seed ? PurchaseReceiptService.Trimmed(input.SupplierLotCode) : null;
        line.RaftId = input.RaftId ?? line.RaftId;
    }

    /// <summary>The receipt as the audit log keeps it: the header, every line and the totals.</summary>
    private static object Snapshot(PurchaseReceipt r)
    {
        var figures = PurchaseMoney.Of(r);
        return new
        {
            r.ReceiptDate, r.ReceivedAt, r.ReceivedByAccountId, r.Notes, r.Discount, r.ZoneId,
            Lines = r.Lines.OrderBy(l => l.LineNumber)
                .Select(l => new { l.LineNumber, l.ItemId, l.Quantity, l.UnitPrice, l.Discount, l.LineAmount, l.SupplierLotCode, l.RaftId }).ToList(),
            Totals = new { figures.Total, figures.Payable, figures.Returned },
        };
    }
}
