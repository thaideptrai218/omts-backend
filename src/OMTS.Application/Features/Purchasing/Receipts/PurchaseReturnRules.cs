using System.Globalization;
using OMTS.Application.Common;
using OMTS.Application.Common.Messages;
using OMTS.Application.Common.Results;
using OMTS.Application.Features.Purchasing.Payments;
using OMTS.Domain.Constants;
using OMTS.Domain.Entities.Purchasing;
using OMTS.Domain.Enums;

namespace OMTS.Application.Features.Purchasing.Receipts;

/// <summary>BR-PURCHASE-07 as pure rules: the fields of a return, the limit of each line (MSG-PURCHASE-03) and the Owner's approval (MSG-PURCHASE-02).</summary>
public static class PurchaseReturnRules
{
    public const int MaxReasonLength = 1000;

    /// <summary>Every missing or invalid field (MSG-GEN-01). "Line n" is the position in the request.</summary>
    public static List<string> InvalidFields(CreatePurchaseReturnRequest request, PurchaseReceipt receipt)
    {
        var invalid = new List<string>();
        if (!DocumentCodes.TypedAllowed(request.Code))
        {
            invalid.Add("Code");
        }

        if (request.Reason?.Trim().Length is null or 0 or > MaxReasonLength)
        {
            invalid.Add("Reason");
        }

        var lines = request.Lines ?? [];
        var known = receipt.Lines.Select(l => l.Id).ToHashSet();
        for (var i = 0; i < lines.Count; i++)
        {
            if (lines[i]?.ReceiptLineId is not { } lineId || !known.Contains(lineId) || !PriceAllowed(lines[i]!.UnitPrice, lines[i]!.Quantity))
            {
                invalid.Add($"Line {i + 1}");
            }
        }

        if (lines.Count == 0 || lines.Select(l => l?.ReceiptLineId).Distinct().Count() != lines.Count)
        {
            invalid.Add("Lines");
        }

        return invalid;
    }

    /// <summary>
    /// What every save of a return checks, Draft or not: the shape of each line. A Draft may have no line and no reason, and its
    /// quantities are not limited until it is completed, because it holds nothing.
    /// </summary>
    public static List<string> InvalidSavedFields(
        SavePurchaseReturnRequest request, PurchaseReceipt? receipt, IReadOnlyDictionary<Guid, FarmingInputItem> items)
    {
        var invalid = new List<string>();
        if (!DocumentCodes.TypedAllowed(request.Code))
        {
            invalid.Add("Code");
        }

        if (request.Reason?.Trim().Length > MaxReasonLength)
        {
            invalid.Add("Reason");
        }

        if (!IsWholeAmount(request.Discount))
        {
            invalid.Add("Discount");
        }

        if (request.Refund is not null && !request.Complete)
        {
            invalid.Add("Refund");
        }

        var lines = request.Lines ?? [];
        if (lines.Count > PurchaseReceiptValidator.MaxLines)
        {
            invalid.Add("Lines");
        }

        var known = receipt?.Lines.Select(l => l.Id).ToHashSet();
        for (var i = 0; i < lines.Count; i++)
        {
            if (!LineIsValid(lines[i], known, items))
            {
                invalid.Add($"Line {i + 1}");
            }
        }

        // One line per receipt line: the limit of MSG-PURCHASE-03 is read line by line.
        if (receipt is not null && lines.Select(l => l?.ReceiptLineId).Distinct().Count() != lines.Count)
        {
            invalid.Add("Lines");
        }

        return invalid;
    }

    private static bool LineIsValid(PurchaseReturnLineInput? line, HashSet<Guid>? receiptLines, IReadOnlyDictionary<Guid, FarmingInputItem> items)
    {
        if (line?.Quantity is not { } quantity || quantity <= 0 || quantity > PurchaseReceiptValidator.MaxQuantity || !PurchaseReceiptValidator.HasAllowedDecimals(quantity))
        {
            return false;
        }

        if (receiptLines is not null)
        {
            return line.ReceiptLineId is { } lineId && receiptLines.Contains(lineId) && PriceAllowed(line.UnitPrice, quantity);
        }

        return line.ReceiptLineId is null
            && line.ItemId is { } itemId && items.TryGetValue(itemId, out var item) && item.StatusId == StatusIds.Usage.InUse
            && (line.ItemUnitId is null || ReceiptLineUnits.Resolve(item, line.ItemUnitId) is not null)
            && line.UnitPrice is { } price && IsWholeAmount(price) && quantity * price <= PurchaseReceiptValidator.MaxAmount;
    }

    /// <summary>Every field missing or invalid to complete the return as it will be saved.</summary>
    public static List<string> InvalidToComplete(PurchaseReturn purchaseReturn, ReturnRefundInput? refund)
    {
        var invalid = new List<string>();
        if (purchaseReturn.Reason.Length == 0)
        {
            invalid.Add("Reason");
        }

        if (purchaseReturn.Lines.Count == 0)
        {
            invalid.Add("Lines");
        }

        var value = purchaseReturn.Lines.Sum(l => l.ReturnValue);
        if (purchaseReturn.Discount > value)
        {
            invalid.Add("Discount");
        }

        invalid.AddRange(InvalidRefund(refund, value - purchaseReturn.Discount));
        return invalid;
    }

    /// <summary>Whole VND above zero and at most the value of the return, by cash or transfer; a transfer names its bank reference.</summary>
    public static List<string> InvalidRefund(ReturnRefundInput? refund, decimal netValue)
    {
        var invalid = new List<string>();
        if (refund is null)
        {
            return invalid;
        }

        if (refund.Amount is not { } amount || amount <= 0 || amount != decimal.Truncate(amount) || amount > netValue)
        {
            invalid.Add("Refund amount");
        }

        if (refund.Method is not (PaymentMethod.Cash or PaymentMethod.BankTransfer))
        {
            invalid.Add("Refund method");
        }

        if (refund.Method == PaymentMethod.BankTransfer
            && SupplierPaymentRules.Trimmed(refund.BankReference)?.Length is null or > SupplierPaymentRules.MaxBankReferenceLength)
        {
            invalid.Add("Refund bank reference");
        }

        return invalid;
    }

    private static bool IsWholeAmount(decimal amount) =>
        amount >= 0 && amount <= PurchaseReceiptValidator.MaxAmount && amount == decimal.Truncate(amount);

    /// <summary>The price a line of a receipt is returned at, when one is entered: whole VND, 0 or more, and an amount the columns hold.</summary>
    public static bool PriceAllowed(decimal? unitPrice, decimal? quantity) =>
        unitPrice is not { } price || (IsWholeAmount(price) && (quantity ?? 0) * price <= PurchaseReceiptValidator.MaxAmount);

    /// <summary>True when the time returned is not later than now (with the clock tolerance of a time received).</summary>
    public static bool ReturnedAtAllowed(DateTimeOffset? returnedAt, DateTimeOffset now) =>
        returnedAt is not { } at || at <= now + PurchaseReceiptValidator.ClockTolerance;

    /// <summary>The time returned at UTC+7 and, from it, the return date the debt and every period filter read.</summary>
    public static void SetReturned(PurchaseReturn purchaseReturn, DateTimeOffset returnedAt)
    {
        purchaseReturn.ReturnedAt = returnedAt.ToOffset(BusinessClock.UtcOffset);
        purchaseReturn.ReturnDate = BusinessClock.ToBusinessDate(returnedAt);
    }

    /// <summary>
    /// The first line whose quantity is refused: a missing one or one with more than 2 decimals is MSG-GEN-01, and one not greater
    /// than 0 or above received − seeded − returned is MSG-PURCHASE-03 with the limit and why.
    /// </summary>
    public static AppError? QuantityError(
        PurchaseReceipt receipt,
        IReadOnlyList<ReturnLineInput> lines,
        IReadOnlyDictionary<Guid, decimal> seeded,
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

            var seededQuantity = seeded.GetValueOrDefault(line.Id);
            var lineReturned = returned.GetValueOrDefault(line.Id);
            var returnable = PurchaseReceiptViews.Returnable(line, seededQuantity, lineReturned);
            // The quantity is typed in the unit of the receipt line; the limit is in base units.
            if (quantity <= 0 || quantity * line.UnitFactor > returnable)
            {
                return OutsideLimit(line, field, returnable, seededQuantity, lineReturned);
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

    private static AppError OutsideLimit(PurchaseReceiptLine line, string field, decimal returnable, decimal seeded, decimal returned)
    {
        var received = line.Quantity ?? 0;
        var baseUnit = line.Item?.Unit ?? line.Unit;
        var seededText = seeded > 0 ? $", seeded {Quantity(seeded)} {baseUnit}" : string.Empty;
        var explanation = $"Received {Quantity(received)} {baseUnit}{seededText}, already returned {Quantity(returned)} {baseUnit}.";
        var itemName = line.Item?.Name ?? string.Empty;
        return new AppError(
            ErrorKind.Validation,
            UserMessages.ReturnQuantityOutsideLimit(itemName, Quantity(returnable), baseUnit, explanation),
            new Dictionary<string, object?>
            {
                ["field"] = field,
                ["receiptLineId"] = line.Id,
                ["item"] = itemName,
                ["maxQuantity"] = returnable,
                ["unit"] = baseUnit,
                ["received"] = received,
                ["seeded"] = seeded,
                ["returned"] = returned,
            });
    }

    private static string Quantity(decimal value) => value.ToString("0.###", CultureInfo.InvariantCulture);
}

