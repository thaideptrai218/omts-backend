using OMTS.Application.Common;
using OMTS.Application.Common.Messages;
using OMTS.Application.Common.Results;
using OMTS.Domain.Entities.Purchasing;
using OMTS.Domain.Enums;

namespace OMTS.Application.Features.Purchasing.Payments;

/// <summary>BR-PURCHASE-08 as pure rules: the fields of a payment, and its two confirmations (409 with the data to show, until the client sends the flag).</summary>
public static class SupplierPaymentRules
{
    public const int MaxBankReferenceLength = 100;
    public const int MaxNoteLength = 1000;
    public const int MaxKeyLength = 100;

    /// <summary>The largest amount the column holds: decimal(18,0).</summary>
    public const decimal MaxAmount = 999_999_999_999_999_999m;

    /// <summary>How far ahead of the server clock a time paid may be: a client clock a little fast is not "in the future".</summary>
    public static readonly TimeSpan ClockTolerance = TimeSpan.FromMinutes(5);

    /// <summary>
    /// Every missing or invalid field (MSG-GEN-01), the supplier, the payer and the receipts of the allocations apart. A
    /// Discontinued supplier can still be paid (GBR-01).
    /// </summary>
    public static List<string> InvalidFields(RecordSupplierPaymentRequest request, DateTimeOffset now)
    {
        var today = BusinessClock.ToBusinessDate(now);
        var invalid = new List<string>();
        if (!DocumentCodes.TypedAllowed(request.Code))
        {
            invalid.Add("Code");
        }

        if (request.Kind is not { } kind || !Enum.IsDefined(kind))
        {
            invalid.Add("Kind");
        }

        var discount = request.DiscountAmount;
        if (discount < 0 || discount > MaxAmount || discount != decimal.Truncate(discount) || (discount > 0 && request.Kind != SupplierPaymentKind.Payment))
        {
            invalid.Add("Discount amount");
        }

        // Whole VND: the column has no decimals, and a silent rounding would record another amount than the bank's.
        // Zero only when the payment is all discount.
        if (request.Amount is not { } money || money < 0 || (money == 0 && discount <= 0) || money > MaxAmount || money != decimal.Truncate(money))
        {
            invalid.Add("Amount");
        }

        if (request.PaidAt is { } paidAt)
        {
            if (!NotInFuture(paidAt, now))
            {
                invalid.Add("Paid at");
            }
            else if (request.PaymentDate is { } date && date != BusinessClock.ToBusinessDate(paidAt))
            {
                invalid.Add("Payment date");
            }
        }
        else if (request.PaymentDate is not { } paymentDate || paymentDate > today)
        {
            invalid.Add("Payment date");
        }

        invalid.AddRange(InvalidVoucher(request.Method, request.BankReference, request.Note));
        if (Trimmed(request.DeduplicationKey)?.Length > MaxKeyLength)
        {
            invalid.Add("Deduplication key");
        }

        invalid.AddRange(InvalidAllocations(request));
        return invalid;
    }

    /// <summary>The method, the bank reference a transfer needs and the note, as a create and an edit check them.</summary>
    public static List<string> InvalidVoucher(PaymentMethod? method, string? bankReference, string? note)
    {
        var invalid = new List<string>();
        if (method is not (PaymentMethod.Cash or PaymentMethod.BankTransfer))
        {
            invalid.Add("Method");
        }

        // Asked only on a transfer (PURCHASE-05.0 step 2): any other method ignores it, whatever its length.
        if (method == PaymentMethod.BankTransfer && Trimmed(bankReference)?.Length is null or > MaxBankReferenceLength)
        {
            invalid.Add("Bank reference");
        }

        if (Trimmed(note)?.Length > MaxNoteLength)
        {
            invalid.Add("Note");
        }

        return invalid;
    }

    /// <summary>True when the time paid is not later than now (with <see cref="ClockTolerance"/>).</summary>
    public static bool NotInFuture(DateTimeOffset paidAt, DateTimeOffset now) => paidAt <= now + ClockTolerance;

    /// <summary>
    /// The time paid: <c>paidAt</c> when sent; otherwise the payment date at the time now when it is today, or at the start of an
    /// earlier day, at UTC+7.
    /// </summary>
    public static DateTimeOffset PaidAt(RecordSupplierPaymentRequest request, DateTimeOffset now)
    {
        if (request.PaidAt is { } paidAt)
        {
            return paidAt.ToOffset(BusinessClock.UtcOffset);
        }

        var date = request.PaymentDate!.Value;
        if (date == BusinessClock.ToBusinessDate(now))
        {
            return now.ToOffset(BusinessClock.UtcOffset);
        }

        // Local midnight of 01/01/0001 is before the first UTC instant, so that day starts at its first instant (07:00 local).
        return BusinessClock.TryFromLocal(date.ToDateTime(TimeOnly.MinValue), out var midnight)
            ? midnight
            : DateTimeOffset.MinValue.ToOffset(BusinessClock.UtcOffset);
    }

    /// <summary>
    /// The shape of the allocations: kind Payment only, one per receipt, whole VND above zero, together at most the amount plus the
    /// discount. Whether each receipt has that much left is read from the ledger by the service.
    /// </summary>
    private static List<string> InvalidAllocations(RecordSupplierPaymentRequest request)
    {
        var invalid = new List<string>();
        if (request.Allocations is not { } allocations)
        {
            return invalid;
        }

        if (allocations.Count > 0 && request.Kind != SupplierPaymentKind.Payment)
        {
            invalid.Add("Allocations");
            return invalid;
        }

        for (var i = 0; i < allocations.Count; i++)
        {
            if (allocations[i] is not { ReceiptId: not null, Amount: { } amount } || amount <= 0 || amount > MaxAmount || amount != decimal.Truncate(amount))
            {
                invalid.Add($"Allocation {i + 1}");
            }
        }

        if (allocations.Select(a => a?.ReceiptId).Distinct().Count() != allocations.Count
            || allocations.Sum(a => a?.Amount ?? 0) > (request.Amount ?? 0) + request.DiscountAmount)
        {
            invalid.Add("Allocations");
        }

        return invalid;
    }

    /// <summary>MSG-PURCHASE-05: more than the debt, or a debt of zero or below; never for money paid back (PURCHASE-05.2).</summary>
    public static AppError? AdvanceConfirmation(RecordSupplierPaymentRequest request, string supplierName, decimal debt)
    {
        var amount = request.Amount!.Value + request.DiscountAmount;
        if (request.ConfirmAdvance || request.Kind != SupplierPaymentKind.Payment || (debt > 0 && amount <= debt))
        {
            return null;
        }

        var advance = amount - debt;
        return AppError.Conflict(
            UserMessages.PaymentAboveDebt(supplierName, Math.Max(debt, 0), advance),
            new Dictionary<string, object?>
            {
                ["confirmFlag"] = "confirmAdvance",
                ["supplierName"] = supplierName,
                ["debtAmount"] = debt,
                ["advanceAmount"] = advance,
            });
    }

    /// <summary>MSG-PURCHASE-06 naming the payment that already has the bank reference (PURCHASE-05.4).</summary>
    public static AppError DuplicateConfirmation(SupplierPayment existing) => AppError.Conflict(
        UserMessages.BankReferenceAlreadyRecorded(existing.Code, existing.Amount, existing.PaymentDate, existing.BankReference!),
        new Dictionary<string, object?>
        {
            ["confirmFlag"] = "confirmDuplicateBankReference",
            ["paymentId"] = existing.Id,
            ["paymentCode"] = existing.Code,
            ["amount"] = existing.Amount,
            ["paymentDate"] = existing.PaymentDate,
            ["bankReference"] = existing.BankReference,
            ["supplierName"] = existing.Supplier.Name,
        });

    public static string? Trimmed(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
