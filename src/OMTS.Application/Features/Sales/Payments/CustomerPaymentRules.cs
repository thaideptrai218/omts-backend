using System.Globalization;
using OMTS.Application.Common;
using OMTS.Application.Common.Messages;
using OMTS.Application.Common.Results;
using OMTS.Application.Features.Purchasing;
using OMTS.Application.Features.Purchasing.Payments;
using OMTS.Application.Features.Sales.Debts;
using OMTS.Domain.Entities.Sales;
using OMTS.Domain.Enums;

namespace OMTS.Application.Features.Sales.Payments;

/// <summary>How a payment comes about: the Accountant records money that arrived, makes a VietQR code first, or counts the voucher of the cash book in the debt.</summary>
public enum CustomerPaymentMode
{
    /// <summary>Money that has arrived (or left); a transfer needs its bank reference.</summary>
    Direct,

    /// <summary>A VietQR code is made first: the payment waits for the transfer and counts nowhere until it arrives.</summary>
    WaitsForTransfer,

    /// <summary>A voucher of the cash book that counts in the debt ("Tính vào công nợ"): it has no bank reference and is for a customer whose debt allows it.</summary>
    FromCashVoucher,

    /// <summary>An advance on an order for money that has arrived (cash, or a transfer with its bank reference): it names the order, no invoice, and no discount.</summary>
    OrderAdvance,
}

/// <summary>
/// BR-DEBT-03 as pure rules: the fields of a payment or money paid back (as BR-PURCHASE-08, same limits as the supplier payments), and its questions and refusals:
/// MSG-DEBT-04 and MSG-DEBT-05 are 409 with the data to show until the client sends the flag named in <c>confirmationField</c>; MSG-DEBT-06 is a 400.
/// </summary>
public static class CustomerPaymentRules
{
    public const int MaxBatchSize = 200;

    /// <summary>How far ahead of the server clock a time paid may be: a client clock a little fast is not "in the future".</summary>
    public static readonly TimeSpan ClockTolerance = SupplierPaymentRules.ClockTolerance;

    /// <summary>
    /// Every missing or invalid field (MSG-GEN-01), the customer, the person who collected and the invoices of the allocations apart. A payment with no money (all discount) needs
    /// no method. <see cref="CustomerPaymentMode.WaitsForTransfer"/>: a VietQR code is made first, so it needs money, the bank account for the code, and no bank reference yet.
    /// </summary>
    /// <param name="forOrder">The payment is an advance on an order (<see cref="CustomerPaymentMode.OrderAdvance"/>, or a VietQR code for one): it carries no discount and names no invoice.</param>
    public static List<string> InvalidFields(
        RecordCustomerPaymentRequest request, DateTimeOffset now, CustomerPaymentKind kind = CustomerPaymentKind.Payment, CustomerPaymentMode mode = CustomerPaymentMode.Direct,
        bool forOrder = false)
    {
        forOrder |= mode == CustomerPaymentMode.OrderAdvance;
        var waitsForTransfer = mode == CustomerPaymentMode.WaitsForTransfer;
        var today = BusinessClock.ToBusinessDate(now);
        var invalid = new List<string>();
        if (!DocumentCodes.TypedAllowed(request.Code))
        {
            invalid.Add("Code");
        }

        if (waitsForTransfer)
        {
            // A code waiting for its transfer takes the time the transfer arrives: no date to check yet.
        }
        else if (request.PaidAt is { } paidAt)
        {
            if (paidAt > now + ClockTolerance)
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

        var discount = request.DiscountAmount;
        if (discount < 0 || discount > SupplierPaymentRules.MaxAmount || discount != decimal.Truncate(discount) || (discount > 0 && (kind != CustomerPaymentKind.Payment || forOrder)))
        {
            invalid.Add("Discount amount");
        }

        // Whole VND: the column has no decimals, and a silent rounding would record another amount than the bank's. Zero only when the payment is all discount.
        if (request.Amount is not { } money || money < 0 || (money == 0 && (discount <= 0 || waitsForTransfer)) || money > SupplierPaymentRules.MaxAmount || money != decimal.Truncate(money))
        {
            invalid.Add("Amount");
        }

        // A missing amount is invalid already; a payment with an amount of 0 (all discount) moves no money and so needs no method.
        var movesMoney = request.Amount != 0m;
        if (waitsForTransfer)
        {
            if (request.CashAccountId is null)
            {
                invalid.Add("Cash account");
            }
        }
        else if (movesMoney)
        {
            if (request.Method is not (CustomerPaymentMethod.Cash or CustomerPaymentMethod.OtherTransfer)
                && !(request.Method == CustomerPaymentMethod.EWallet && mode == CustomerPaymentMode.FromCashVoucher))
            {
                invalid.Add("Method");
            }

            if (mode is CustomerPaymentMode.Direct or CustomerPaymentMode.OrderAdvance && request.Method == CustomerPaymentMethod.OtherTransfer
                && Trimmed(request.BankReference)?.Length is null or > SupplierPaymentRules.MaxBankReferenceLength)
            {
                invalid.Add("Bank reference");
            }
        }

        if (Trimmed(request.Note)?.Length > SupplierPaymentRules.MaxNoteLength)
        {
            invalid.Add("Note");
        }

        if (Trimmed(request.DeduplicationKey)?.Length > SupplierPaymentRules.MaxKeyLength)
        {
            invalid.Add("Deduplication key");
        }

        invalid.AddRange(InvalidAllocations(request, kind));
        if (forOrder && request.Allocations is { Count: > 0 })
        {
            invalid.Add("Allocations");
        }

        return invalid;
    }

    /// <summary>
    /// The time paid: <c>paidAt</c> when sent; otherwise the payment date at the time now when it is today, or at the start of an earlier day, at UTC+7.
    /// </summary>
    public static DateTimeOffset PaidAt(RecordCustomerPaymentRequest request, DateTimeOffset now)
    {
        if (request.PaidAt is { } paidAt)
        {
            return paidAt.ToOffset(BusinessClock.UtcOffset);
        }

        var date = request.PaymentDate!.Value;
        return date == BusinessClock.ToBusinessDate(now) ? now.ToOffset(BusinessClock.UtcOffset) : BusinessClock.FromLocal(date.ToDateTime(TimeOnly.MinValue));
    }

    /// <summary>
    /// The shape of the allocations: kind Payment only, one per invoice, whole VND above zero, together at most the money plus the discount. Whether each invoice has that much
    /// left is read from the debt by the service.
    /// </summary>
    private static List<string> InvalidAllocations(RecordCustomerPaymentRequest request, CustomerPaymentKind kind)
    {
        var invalid = new List<string>();
        if (request.Allocations is not { } allocations)
        {
            return invalid;
        }

        if (allocations.Count > 0 && kind != CustomerPaymentKind.Payment)
        {
            invalid.Add("Allocations");
            return invalid;
        }

        for (var i = 0; i < allocations.Count; i++)
        {
            if (allocations[i] is not { InvoiceId: not null, Amount: { } amount } || amount <= 0 || amount > SupplierPaymentRules.MaxAmount || amount != decimal.Truncate(amount))
            {
                invalid.Add($"Allocation {i + 1}");
            }
        }

        if (allocations.Select(a => a?.InvoiceId).Distinct().Count() != allocations.Count
            || allocations.Sum(a => a?.Amount ?? 0) > (request.Amount ?? 0) + request.DiscountAmount)
        {
            invalid.Add("Allocations");
        }

        return invalid;
    }

    /// <summary>The reason of a cancellation (DEBT-02.4.E1), trimmed; null when missing or too long.</summary>
    public static string? Reason(string? reason) => Trimmed(reason) is { Length: <= SupplierPaymentRules.MaxNoteLength } kept ? kept : null;

    /// <summary>MSG-DEBT-04 (DEBT-02.1): a payment above the debt, or with a debt of 0 or below; the difference becomes money owed back.</summary>
    /// <param name="amount">What the payment takes off the debt: the money and its discount.</param>
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
    public static decimal DebtAfter(decimal debt, CustomerPaymentKind kind, decimal amount, bool cancelled, decimal discount = 0m) =>
        (kind == CustomerPaymentKind.Payment) != cancelled ? debt - amount - discount : debt + amount + discount;

    /// <summary>The atomic {transaction_kind} of MSG-DEBT-03.</summary>
    public static string TransactionKind(CustomerPaymentKind kind, bool cancelled) =>
        $"{(kind == CustomerPaymentKind.PaidBack ? "PaidBack" : "Payment")}{(cancelled ? "Cancelled" : string.Empty)}";

    /// <summary>
    /// The note a transfer that arrived for a payment waiting for it gets when it is not what was asked: "Khách chuyển 800.000, phiếu 1.000.000" (what the customer
    /// transferred, what the voucher asked).
    /// </summary>
    public static string TransferDifferenceNote(decimal received, decimal asked) =>
        $"Khách chuyển {Vnd(received)}, phiếu {Vnd(asked)}";

    /// <summary>
    /// A transfer short of the payment: its parts are cut from the newest invoice back until they are covered by the money received and the discount; one that is over
    /// leaves them as they are (the rest is an advance).
    /// </summary>
    /// <param name="parts">The parts with the time of their invoice; returns the parts that remain (above zero), in the same order.</param>
    public static IReadOnlyList<(Guid InvoiceId, decimal Amount)> TrimParts(
        IReadOnlyList<(Guid InvoiceId, DateTimeOffset InvoicedAt, string InvoiceCode, decimal Amount)> parts, decimal covered)
    {
        var excess = parts.Sum(p => p.Amount) - covered;
        var kept = parts.ToDictionary(p => p.InvoiceId, p => p.Amount);
        foreach (var part in parts.OrderByDescending(p => p.InvoicedAt).ThenByDescending(p => p.InvoiceCode, StringComparer.Ordinal))
        {
            if (excess <= 0)
            {
                break;
            }

            var cut = Math.Min(excess, part.Amount);
            kept[part.InvoiceId] -= cut;
            excess -= cut;
        }

        return parts.Where(p => kept[p.InvoiceId] > 0).Select(p => (p.InvoiceId, kept[p.InvoiceId])).ToList();
    }

    private static string Vnd(decimal amount) => amount.ToString("#,##0", CultureInfo.InvariantCulture).Replace(',', '.');

    public static string? Trimmed(string? value) => SupplierPaymentRules.Trimmed(value);
}
