using OMTS.Application.Common.Messages;
using OMTS.Application.Common.Results;
using OMTS.Application.Features.Purchasing.Payments;
using OMTS.Application.Features.Sales.Debts;
using OMTS.Domain.Entities.Sales;
using OMTS.Domain.Enums;

namespace OMTS.Application.Features.Sales.Payments;

/// <summary>
/// BR-DEBT-03 as pure rules: the fields of a payment or money paid back (as BR-PURCHASE-08, same limits as the supplier payments), and its questions and refusals:
/// MSG-DEBT-04 and MSG-DEBT-05 are 409 with the data to show until the client sends the flag named in <c>confirmationField</c>; MSG-DEBT-06 is a 400.
/// </summary>
public static class CustomerPaymentRules
{
    public const int MaxBatchSize = 200;

    /// <summary>Every missing or invalid field (MSG-GEN-01), the customer and the cash collector apart.</summary>
    public static List<string> InvalidFields(RecordCustomerPaymentRequest request, DateOnly today)
    {
        var invalid = new List<string>();
        if (request.PaymentDate is not { } paymentDate || paymentDate > today)
        {
            invalid.Add("Payment date");
        }

        // Whole VND: the column has no decimals, and a silent rounding would record another amount than the bank's.
        if (request.Amount is not > 0 || request.Amount > SupplierPaymentRules.MaxAmount || request.Amount != decimal.Truncate(request.Amount.Value))
        {
            invalid.Add("Amount");
        }

        if (request.Method is not (CustomerPaymentMethod.Cash or CustomerPaymentMethod.OtherTransfer))
        {
            invalid.Add("Method");
        }

        if (request.Method == CustomerPaymentMethod.OtherTransfer && Trimmed(request.BankReference)?.Length is null or > SupplierPaymentRules.MaxBankReferenceLength)
        {
            invalid.Add("Bank reference");
        }

        if (Trimmed(request.Note)?.Length > SupplierPaymentRules.MaxNoteLength)
        {
            invalid.Add("Note");
        }

        if (Trimmed(request.DeduplicationKey)?.Length > SupplierPaymentRules.MaxKeyLength)
        {
            invalid.Add("Deduplication key");
        }

        return invalid;
    }

    /// <summary>The reason of a cancellation (DEBT-02.4.E1), trimmed; null when missing or too long.</summary>
    public static string? Reason(string? reason) => Trimmed(reason) is { Length: <= SupplierPaymentRules.MaxNoteLength } kept ? kept : null;

    /// <summary>MSG-DEBT-04 (DEBT-02.1): a payment above the debt, or with a debt of 0 or below; the difference becomes money owed back.</summary>
    public static AppError? AboveDebtQuestion(string customerName, decimal debt, decimal amount, bool confirmed)
    {
        if (confirmed || (debt > 0 && amount <= debt))
        {
            return null;
        }

        var owedBack = CustomerDebtMath.OwedBack(debt - amount);
        return AppError.Conflict(
            UserMessages.CustomerPaymentAboveDebt(customerName, debt, amount, owedBack),
            new Dictionary<string, object?>
            {
                ["confirmationField"] = "confirmAboveDebt",
                ["customerName"] = customerName,
                ["debtAmount"] = debt,
                ["amount"] = amount,
                ["owedBackAmount"] = owedBack,
            });
    }

    /// <summary>MSG-DEBT-06 (DEBT-02.3.E1): money paid back only up to the money owed back.</summary>
    public static AppError? PaidBackAboveOwedBack(string customerName, decimal debt, decimal amount)
    {
        var owedBack = CustomerDebtMath.OwedBack(debt);
        return amount <= owedBack
            ? null
            : new AppError(
                ErrorKind.Validation,
                UserMessages.PaidBackAboveOwedBack(customerName, owedBack),
                new Dictionary<string, object?> { ["field"] = "Amount", ["owedBackAmount"] = owedBack });
    }

    /// <summary>MSG-DEBT-05 (DEBT-02.5) naming the payment that already has the bank reference.</summary>
    public static AppError DuplicateBankReferenceQuestion(CustomerPayment existing) => AppError.Conflict(
        UserMessages.CustomerBankReferenceAlreadyRecorded(existing.Code, existing.Amount, existing.PaymentDate, existing.Customer?.Name, existing.BankReference!),
        new Dictionary<string, object?>
        {
            ["confirmationField"] = "confirmDuplicateBankReference",
            ["paymentId"] = existing.Id,
            ["paymentCode"] = existing.Code,
            ["amount"] = existing.Amount,
            ["paymentDate"] = existing.PaymentDate,
            ["customerName"] = existing.Customer?.Name,
            ["bankReference"] = existing.BankReference,
        });

    /// <summary>The debt after a payment (lower) or money paid back (higher), and after its cancellation (the other way).</summary>
    public static decimal DebtAfter(decimal debt, CustomerPaymentKind kind, decimal amount, bool cancelled) =>
        (kind == CustomerPaymentKind.Payment) != cancelled ? debt - amount : debt + amount;

    /// <summary>The atomic {transaction_kind} of MSG-DEBT-03.</summary>
    public static string TransactionKind(CustomerPaymentKind kind, bool cancelled) =>
        $"{(kind == CustomerPaymentKind.PaidBack ? "PaidBack" : "Payment")}{(cancelled ? "Cancelled" : string.Empty)}";

    public static string? Trimmed(string? value) => SupplierPaymentRules.Trimmed(value);
}
