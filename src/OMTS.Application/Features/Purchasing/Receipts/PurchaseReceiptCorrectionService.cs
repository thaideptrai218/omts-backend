using OMTS.Application.Common.Concurrency;
using OMTS.Application.Common.Interfaces;
using OMTS.Application.Common.Interfaces.Persistence;
using OMTS.Application.Common.Messages;
using OMTS.Application.Common.Results;
using OMTS.Application.Features.Audit;
using OMTS.Application.Features.Audit.Models;
using OMTS.Domain.Constants;
using OMTS.Domain.Entities.Purchasing;
using OMTS.Domain.Enums;

namespace OMTS.Application.Features.Purchasing.Receipts;

/// <summary>
/// PURCHASE-06.2 – the Owner corrects a Completed receipt that can no longer be cancelled (a seed lot seeded, or a purchase
/// return that is not cancelled): the unit price, the quantity (not below seeded + returned, MSG-PURCHASE-10) and the supplier's
/// lot code of its lines, with a reason; never the supplier or the items (BR-PURCHASE-09). The audit log keeps the values before
/// and after. The supplier debt and raft material costs follow the new line amounts, while the returns already recorded keep
/// the amounts they had (PM 03/11); no notification is sent (BR-PURCHASE-05).
/// </summary>
public sealed class PurchaseReceiptCorrectionService(
    IPurchaseReceiptRepository receipts,
    PurchaseReceiptCancellationService cancellations,
    PurchaseReceiptViews views,
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

        if (inputs.Count == 0 || inputs.Any(l => l.LineId is not { } id || !lines.ContainsKey(id)) || inputs.Select(l => l.LineId).Distinct().Count() != inputs.Count)
        {
            invalid.Add("Lines");
        }

        if (invalid.Count > 0)
        {
            return AppError.InvalidFields(invalid);
        }

        var seeded = await receipts.SeededAsync(receipt.Lines.Select(l => l.Id).ToList(), cancellationToken);
        var returned = PurchaseReceiptViews.ReturnedQuantities(receipt);
        foreach (var input in inputs.OrderBy(l => lines[l.LineId!.Value].LineNumber))
        {
            var line = lines[input.LineId!.Value];
            if (CheckLine(line, input, seeded.GetValueOrDefault(line.Id), returned.GetValueOrDefault(line.Id)) is { } lineError)
            {
                return lineError;
            }
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
            line.Quantity = input.Quantity ?? line.Quantity;
            line.UnitPrice = input.UnitPrice ?? line.UnitPrice;
            if (input.SupplierLotCode is { } lotCode)
            {
                line.SupplierLotCode = string.IsNullOrWhiteSpace(lotCode) ? null : lotCode.Trim();
            }

            line.LineAmount = PurchaseMoney.LineAmount(line.Quantity, line.UnitPrice);
            after.Add(LineValues(line));
        }

        // Only lines change: the receipt still gets a new row version, so a parallel return or correction is MSG06.
        unitOfWork.MarkChanged(receipt);
        audit.Record(new AuditEntry("PurchaseReceipt.Corrected", nameof(PurchaseReceipt))
        {
            RecordId = receipt.Id, RecordCode = receipt.Code, Before = new { Lines = before }, After = new { Lines = after }, Reason = reason,
        });
        await unitOfWork.SaveChangesAsync(cancellationToken);
        return await views.ViewAsync(receipt.Id, cancellationToken);
    }

    /// <summary>BR-PURCHASE-01 values, and a quantity never below seeded + returned (MSG-PURCHASE-10).</summary>
    private static AppError? CheckLine(PurchaseReceiptLine line, CorrectionLineInput input, (decimal Seeded, bool UnknownQuantity) seeded, decimal returned)
    {
        var field = $"Line {line.LineNumber}";
        var (quantity, price) = (input.Quantity ?? line.Quantity ?? 0, input.UnitPrice ?? line.UnitPrice ?? 0);
        // A quantity saved before the 2-decimal rule and left unchanged does not stop the Owner correcting the price of the line.
        if (quantity <= 0 || quantity > PurchaseReceiptValidator.MaxQuantity || (quantity != line.Quantity && !PurchaseReceiptValidator.HasAllowedDecimals(quantity))
            || price < 0 || price > PurchaseReceiptValidator.MaxAmount || decimal.Truncate(price) != price
            || quantity * price > PurchaseReceiptValidator.MaxAmount)
        {
            return PurchaseReceiptValidator.InvalidLine(line.LineNumber);
        }

        if (input.SupplierLotCode is { } lotCode && (line.Item?.Group != InputItemGroup.Seed || lotCode.Trim().Length > 100))
        {
            return AppError.Invalid($"{field} supplier lot code");
        }

        // A seeding without a quantity counts as the whole line seeded: its quantity cannot be lowered.
        var seededQuantity = seeded.UnknownQuantity ? line.Quantity ?? 0 : seeded.Seeded;
        var minimum = seededQuantity + returned;
        return quantity >= minimum
            ? null
            : new AppError(
                ErrorKind.Validation,
                UserMessages.QuantityBelowUsed(line.Item?.Name ?? field, minimum, line.Unit, seededQuantity, returned),
                new Dictionary<string, object?>
                {
                    ["field"] = field, ["minQuantity"] = minimum, ["seededQuantity"] = seededQuantity, ["returnedQuantity"] = returned,
                });
    }

    private static object LineValues(PurchaseReceiptLine line) =>
        new { line.LineNumber, line.Quantity, line.UnitPrice, line.LineAmount, line.SupplierLotCode };

    private AppError WrongStatus(PurchaseReceipt receipt, string? hint) => AppError.Conflict(
        UserMessages.WrongStatus($"Purchase receipt {receipt.Code}", statusCatalog.DisplayNameOf(receipt.StatusId)),
        hint is null ? null : new Dictionary<string, object?> { ["hint"] = hint });
}
