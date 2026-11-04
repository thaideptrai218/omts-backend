using System.Globalization;
using OMTS.Application.Common.Messages;
using OMTS.Application.Common.Results;
using OMTS.Domain.Entities.Purchasing;

namespace OMTS.Application.Features.Purchasing.Receipts;

/// <summary>BR-PURCHASE-07 as pure rules: the fields of a return, the limit of each line (MSG-PURCHASE-03) and the Owner's approval (MSG-PURCHASE-02).</summary>
public static class PurchaseReturnRules
{
    public const int MaxReasonLength = 1000;

    /// <summary>Every missing or invalid field (MSG-GEN-01). "Line n" is the position in the request.</summary>
    public static List<string> InvalidFields(CreatePurchaseReturnRequest request, PurchaseReceipt receipt)
    {
        var invalid = new List<string>();
        if (request.Reason?.Trim().Length is null or 0 or > MaxReasonLength)
        {
            invalid.Add("Reason");
        }

        var lines = request.Lines ?? [];
        var known = receipt.Lines.Select(l => l.Id).ToHashSet();
        for (var i = 0; i < lines.Count; i++)
        {
            if (lines[i].ReceiptLineId is not { } lineId || !known.Contains(lineId))
            {
                invalid.Add($"Line {i + 1}");
            }
        }

        if (lines.Count == 0 || lines.Select(l => l.ReceiptLineId).Distinct().Count() != lines.Count)
        {
            invalid.Add("Lines");
        }

        return invalid;
    }

    /// <summary>
    /// The first line whose quantity is refused: a missing one or one with more than 2 decimals is MSG-GEN-01, and one not greater
    /// than 0 or above received − seeded − returned is MSG-PURCHASE-03 with the limit and why.
    /// </summary>
    public static AppError? QuantityError(
        PurchaseReceipt receipt,
        IReadOnlyList<ReturnLineInput> lines,
        IReadOnlyDictionary<Guid, (decimal Seeded, bool UnknownQuantity)> seeded,
        IReadOnlyDictionary<Guid, decimal> returned)
    {
        for (var i = 0; i < lines.Count; i++)
        {
            var field = $"Line {i + 1}";
            var line = receipt.Lines.First(l => l.Id == lines[i].ReceiptLineId);
            if (lines[i].Quantity is not { } quantity || !PurchaseReceiptValidator.HasAllowedDecimals(quantity))
            {
                return new AppError(
                    ErrorKind.Validation, UserMessages.Invalid(field) with { LegacyCode = MessageCodes.Legacy.Msg59 }, new Dictionary<string, object?> { ["field"] = field });
            }

            var (seededQuantity, unknown) = seeded.GetValueOrDefault(line.Id);
            var lineReturned = returned.GetValueOrDefault(line.Id);
            var returnable = PurchaseReceiptViews.Returnable(line, seededQuantity, unknown, lineReturned);
            if (quantity <= 0 || quantity > returnable)
            {
                return OutsideLimit(line, field, returnable, seededQuantity, unknown, lineReturned);
            }
        }

        return null;
    }

    /// <summary>MSG-PURCHASE-02: nothing is saved until the same request comes back with the flag (PURCHASE-02.0.E3).</summary>
    public static AppError OwnerApprovalConfirmation(decimal returnAmount, string supplierName) => AppError.Conflict(
        UserMessages.ReturnOwnerApproval(returnAmount, supplierName),
        new Dictionary<string, object?>
        {
            ["confirmFlag"] = "ownerApproved",
            ["returnAmount"] = returnAmount,
            ["supplierName"] = supplierName,
        });

    private static AppError OutsideLimit(PurchaseReceiptLine line, string field, decimal returnable, decimal seeded, bool unknownSeeded, decimal returned)
    {
        var received = line.Quantity ?? 0;
        var seededText = seeded > 0 ? $", seeded {Quantity(seeded)} {line.Unit}" : string.Empty;
        var explanation = unknownSeeded
            ? "The quantity already seeded from this line is not known."
            : $"Received {Quantity(received)} {line.Unit}{seededText}, already returned {Quantity(returned)} {line.Unit}.";
        var itemName = line.Item?.Name ?? string.Empty;
        return new AppError(
            ErrorKind.Validation,
            UserMessages.ReturnQuantityOutsideLimit(itemName, Quantity(returnable), line.Unit, explanation),
            new Dictionary<string, object?>
            {
                ["field"] = field,
                ["receiptLineId"] = line.Id,
                ["item"] = itemName,
                ["maxQuantity"] = returnable,
                ["unit"] = line.Unit,
                ["received"] = received,
                ["seeded"] = unknownSeeded ? received : seeded,
                ["returned"] = returned,
            });
    }

    private static string Quantity(decimal value) => value.ToString("0.###", CultureInfo.InvariantCulture);
}
