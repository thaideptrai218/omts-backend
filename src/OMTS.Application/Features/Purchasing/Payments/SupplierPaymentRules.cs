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

    /// <summary>Every missing or invalid field (MSG-GEN-01), the supplier apart. A Discontinued supplier can still be paid (GBR-01).</summary>
    public static List<string> InvalidFields(RecordSupplierPaymentRequest request, DateOnly today)
    {
        var invalid = new List<string>();
        if (request.Kind is not { } kind || !Enum.IsDefined(kind))
        {
            invalid.Add("Kind");
        }

        // Whole VND: the column has no decimals, and a silent rounding would record another amount than the bank's.
        if (request.Amount is not > 0 || request.Amount > MaxAmount || request.Amount != decimal.Truncate(request.Amount.Value))
        {
            invalid.Add("Amount");
        }

        if (request.PaymentDate is not { } paymentDate || paymentDate > today)
        {
            invalid.Add("Payment date");
        }

        if (request.Method is not (PaymentMethod.Cash or PaymentMethod.BankTransfer))
        {
            invalid.Add("Method");
        }

        var reference = Trimmed(request.BankReference);
        if ((request.Method == PaymentMethod.BankTransfer && reference is null) || reference?.Length > MaxBankReferenceLength)
        {
            invalid.Add("Bank reference");
        }

        if (Trimmed(request.Note)?.Length > MaxNoteLength)
        {
            invalid.Add("Note");
        }

        if (Trimmed(request.DeduplicationKey)?.Length > MaxKeyLength)
        {
            invalid.Add("Deduplication key");
        }

        return invalid;
    }

    /// <summary>MSG-PURCHASE-05: more than the debt, or a debt of zero or below; never for money paid back (PURCHASE-05.2).</summary>
    public static AppError? AdvanceConfirmation(RecordSupplierPaymentRequest request, string supplierName, decimal debt)
    {
        var amount = request.Amount!.Value;
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
