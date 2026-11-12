using OMTS.Application.Common.Concurrency;
using OMTS.Application.Common.Interfaces;
using OMTS.Application.Common.Interfaces.Persistence;
using OMTS.Application.Common.Messages;
using OMTS.Application.Common.Results;
using OMTS.Application.Features.Audit;
using OMTS.Application.Features.Audit.Models;
using OMTS.Application.Features.Purchasing.Items;
using OMTS.Domain.Constants;
using OMTS.Domain.Entities.Purchasing;
using OMTS.Domain.Enums;

namespace OMTS.Application.Features.Purchasing.Receipts;

/// <summary>
/// PURCHASE-06.2 – the Owner corrects a Completed receipt that can no longer be cancelled (a seed lot seeded, or a purchase
/// return that is not cancelled): the unit price, the quantity (not below seeded + returned, MSG-PURCHASE-10) and the supplier's
/// lot code of its lines, with a reason; never the supplier or the items (BR-PURCHASE-09). A line keeps its discount per unit, and
/// its unit price is never set below it. The audit log keeps the values before and after. The supplier debt follows the new
/// line amounts, while the returns already recorded keep the amounts they had (PM 03/11); no notification is sent
/// (BR-PURCHASE-05). The fuller edit of a Completed receipt is <see cref="PurchaseReceiptEditService"/>.
/// </summary>
public sealed class PurchaseReceiptCorrectionService(
    IPurchaseReceiptRepository receipts,
    ISeedLotUsageQuery seedUsage,
    PurchaseReceiptCancellationService cancellations,
    PurchaseReceiptViews views,
    InputStockLedger ledger,
    IStatusCatalog statusCatalog,
    RowVersionGuard rowVersions,
    AuditLogService audit,
    IUnitOfWork unitOfWork)
{
    public async Task<Result<PurchaseReceiptView>> CorrectAsync(Guid receiptId, CorrectReceiptRequest request, CancellationToken cancellationToken)
    {
        var receipt = await receipts.GetForEditAsync(receiptId, cancellationToken);
        if (receipt is null)
        {
            return AppError.NotFound;
        }

        if (receipt.StatusId != StatusIds.PurchaseReceipt.Completed)
        {
            return WrongStatus(receipt, null);
        }

        if (await cancellations.BlockAsync(receipt, cancellationToken) is null)
        {
            // To change a receipt that can still be cancelled, the user cancels it and copies it (BR-PURCHASE-09).
            return WrongStatus(receipt, "The receipt can still be cancelled: cancel it and copy it into a new receipt.");
        }

        var reason = request.Reason?.Trim() ?? string.Empty;
        var inputs = request.Lines ?? [];
        var lines = receipt.Lines.ToDictionary(l => l.Id);
        var invalid = new List<string>();
        if (reason.Length is 0 or > 1000)
        {
            invalid.Add("Reason");
        }

        if (inputs.Count == 0 || inputs.Any(l => l?.LineId is not { } id || !lines.ContainsKey(id)) || inputs.Select(l => l?.LineId).Distinct().Count() != inputs.Count)
        {
            invalid.Add("Lines");
        }

        if (invalid.Count > 0)
        {
            return AppError.InvalidFields(invalid);
        }

        var seeded = await seedUsage.SeededAsync(receipt.Lines.Select(l => l.Id).ToList(), cancellationToken);
        var returned = PurchaseReceiptViews.ReturnedQuantities(receipt);
        foreach (var input in inputs.OrderBy(l => lines[l.LineId!.Value].LineNumber))
        {
            var line = lines[input.LineId!.Value];
            if (CheckLine(line, input, seeded.GetValueOrDefault(line.Id), returned.GetValueOrDefault(line.Id)) is { } lineError)
            {
                return lineError;
            }
        }

        var corrected = receipt.Lines.Sum(line => inputs.FirstOrDefault(i => i.LineId == line.Id) is { } input
            ? PurchaseMoney.LineAmount(input.Quantity ?? line.EnteredQuantity, input.UnitPrice ?? line.EnteredUnitPrice, line.EnteredDiscount)
            : line.LineAmount);
        if (receipt.Discount > corrected)
        {
            return AppError.Invalid("Discount");
        }

        if (request.RowVersion is not null && await rowVersions.CheckAsync(receipt, request.RowVersion, cancellationToken) is { } conflict)
        {
            return conflict;
        }

        var before = new List<object>();
        var after = new List<object>();
        foreach (var input in inputs)
        {
            var line = lines[input.LineId!.Value];
            before.Add(LineValues(line));
            ReceiptLineUnits.Fill(line, input.Quantity ?? line.EnteredQuantity, input.UnitPrice ?? line.EnteredUnitPrice, line.EnteredDiscount);
            if (input.SupplierLotCode is { } lotCode)
            {
                line.SupplierLotCode = string.IsNullOrWhiteSpace(lotCode) ? null : lotCode.Trim();
            }

            after.Add(LineValues(line));
        }

        // The ledger follows the corrected quantities: the old rows are undone, the new ones written.
        await ledger.ReverseAsync(receipt.Id, afterReceiptRemoved: false, cancellationToken);
        await ledger.PostReceiptAsync(receipt, cancellationToken);

        // Only lines change: the receipt still gets a new row version, so a parallel return or correction is MSG06.
        unitOfWork.MarkChanged(receipt);
        audit.Record(new AuditEntry("PurchaseReceipt.Corrected", nameof(PurchaseReceipt))
        {
            RecordId = receipt.Id, RecordCode = receipt.Code, Before = new { Lines = before }, After = new { Lines = after }, Reason = reason,
        });
        await unitOfWork.SaveChangesAsync(cancellationToken);
        return await views.ViewAsync(receipt.Id, cancellationToken);
    }

    /// <summary>BR-PURCHASE-01 values, and a changed quantity never below seeded + returned (MSG-PURCHASE-10).</summary>
    private static AppError? CheckLine(PurchaseReceiptLine line, CorrectionLineInput input, decimal seededQuantity, decimal returned)
    {
        var field = $"Line {line.LineNumber}";
        var (quantity, price) = (input.Quantity ?? line.EnteredQuantity ?? 0, input.UnitPrice ?? line.EnteredUnitPrice ?? 0);
        // A quantity saved before the 2-decimal rule and left unchanged does not stop the Owner correcting the price of the line.
        if (quantity <= 0 || quantity > PurchaseReceiptValidator.MaxQuantity || (quantity != line.EnteredQuantity && !PurchaseReceiptValidator.HasAllowedDecimals(quantity))
            || price < line.EnteredDiscount || price > PurchaseReceiptValidator.MaxAmount || decimal.Truncate(price) != price
            || quantity * price > PurchaseReceiptValidator.MaxAmount)
        {
            return PurchaseReceiptValidator.InvalidLine(line.LineNumber);
        }

        if (input.SupplierLotCode is { } lotCode && (line.Item?.Group != InputItemGroup.Seed || lotCode.Trim().Length > 100))
        {
            return AppError.Invalid($"{field} supplier lot code");
        }

        // Seeding does not compare with what is left (GBR-04), so seeded + returned can pass the quantity: that must not stop a price correction.
        if (quantity == line.EnteredQuantity)
        {
            return null;
        }

        // Seeded and returned are in base units, like the quantity of the line.
        return quantity * line.UnitFactor >= seededQuantity + returned ? null : PurchaseReceiptValidator.QuantityBelowUsed(line, field, seededQuantity, returned);
    }

    private static object LineValues(PurchaseReceiptLine line) =>
        new { line.LineNumber, Quantity = line.EnteredQuantity, UnitPrice = line.EnteredUnitPrice, Discount = line.EnteredDiscount, line.LineAmount, line.SupplierLotCode };

    private AppError WrongStatus(PurchaseReceipt receipt, string? hint) => AppError.Conflict(
        UserMessages.WrongStatus($"Purchase receipt {receipt.Code}", statusCatalog.DisplayNameOf(receipt.StatusId)),
        hint is null ? null : new Dictionary<string, object?> { ["hint"] = hint });
}

