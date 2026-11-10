using OMTS.Application.Common.Constants;
using OMTS.Application.Features.Purchasing;
using OMTS.Application.Features.Purchasing.Payments;
using OMTS.Domain.Entities.CashBook;
using OMTS.Domain.Enums;

namespace OMTS.Application.Features.CashBook;

/// <summary>The fields of a self-made voucher, a voucher type, a cash account and a party as pure rules; each invalid field is named as on the screens (MSG-GEN-01).</summary>
public static class CashBookRules
{
    public const int MaxNoteLength = 1000;
    public const int MaxNameLength = 200;
    public const int MaxAccountNumberLength = 50;
    public const int MaxPhoneLength = 20;
    public const int MaxAddressLength = 500;
    public const int MaxReasonLength = 1000;

    /// <summary>The largest amount the column holds: decimal(18,0).</summary>
    public const decimal MaxAmount = SupplierPaymentRules.MaxAmount;

    /// <summary>The sequence key and so the prefix of a generated code: TTM/CTM for Cash, TNH/CNH for Bank, TVD/CVD for EWallet (receipt/payment).</summary>
    public static string SequenceKey(CashFund fund, CashDirection direction) => (fund, direction) switch
    {
        (CashFund.Cash, CashDirection.Receipt) => DocumentSequenceKeys.CashReceiptCash,
        (CashFund.Cash, _) => DocumentSequenceKeys.CashPaymentCash,
        (CashFund.Bank, CashDirection.Receipt) => DocumentSequenceKeys.CashReceiptBank,
        (CashFund.Bank, _) => DocumentSequenceKeys.CashPaymentBank,
        (_, CashDirection.Receipt) => DocumentSequenceKeys.CashReceiptEWallet,
        _ => DocumentSequenceKeys.CashPaymentEWallet,
    };

    /// <summary>The prefix followed by at least six digits: TTM000001…TTM999999, then TTM1000000. The number comes from the sequence, so a code is never given twice.</summary>
    public static string GeneratedCode(CashFund fund, CashDirection direction, long number) =>
        $"{SequenceKey(fund, direction)}{number.ToString("000000", System.Globalization.CultureInfo.InvariantCulture)}";

    /// <summary>
    /// Every missing or invalid field of a voucher. The type and the account are the records the request names (null when none
    /// was found). The stored ids are those of the voucher being edited: its type and account may still be named once hidden or
    /// out of use, as they were when it was made.
    /// </summary>
    public static List<string> InvalidVoucher(
        SaveCashVoucherRequest request, CashVoucherType? type, CashAccount? account, DateTimeOffset now, Guid? storedTypeId = null, Guid? storedAccountId = null)
    {
        var invalid = new List<string>();
        if (!DocumentCodes.TypedAllowed(request.Code))
        {
            invalid.Add("Code");
        }

        if (request.Direction is not { } direction || !Enum.IsDefined(direction))
        {
            invalid.Add("Direction");
        }

        if (request.Fund is not { } fund || !Enum.IsDefined(fund))
        {
            invalid.Add("Fund");
        }
        else if (!AccountMatchesFund(fund, request.CashAccountId, account, storedAccountId))
        {
            invalid.Add("Cash account");
        }

        // A type the system writes itself (supplier payments, wages) cannot be chosen by hand, and the type is of the voucher's direction.
        if (request.TypeId is null || type is null || !type.Manual || (request.Direction is { } d && type.Direction != d)
            || (!type.IsActive && type.Id != storedTypeId))
        {
            invalid.Add("Type");
        }

        // Whole VND: the column has no decimals, and a silent rounding would record another amount than the bank's.
        if (request.Amount is not { } amount || amount <= 0 || amount > MaxAmount || amount != decimal.Truncate(amount))
        {
            invalid.Add("Amount");
        }

        if (request.OccurredAt is not { } occurredAt || !SupplierPaymentRules.NotInFuture(occurredAt, now))
        {
            invalid.Add("Occurred at");
        }

        if (SupplierPaymentRules.Trimmed(request.Note)?.Length > MaxNoteLength)
        {
            invalid.Add("Note");
        }

        if ((request.PartyKind is null) != (request.PartyId is null)
            || (request.PartyKind is { } kind && !Enum.IsDefined(kind)))
        {
            invalid.Add("Party");
        }

        return invalid;
    }

    /// <summary>Cash has no account; Bank has an active Bank account and EWallet an active EWallet account (or the one the voucher already has).</summary>
    public static bool AccountMatchesFund(CashFund fund, Guid? accountId, CashAccount? account, Guid? storedAccountId = null)
    {
        if (fund == CashFund.Cash)
        {
            return accountId is null;
        }

        var kind = fund == CashFund.Bank ? CashAccountKind.Bank : CashAccountKind.EWallet;
        return accountId is not null && account is not null && account.Kind == kind && (account.IsActive || account.Id == storedAccountId);
    }

    /// <summary>The fields of a cash account; a Bank account takes its provider from the bank list, so only an EWallet needs a provider name.</summary>
    public static List<string> InvalidAccount(SaveCashAccountRequest request, string? bankShortName)
    {
        var invalid = new List<string>();
        if (request.Kind is not { } kind || !Enum.IsDefined(kind))
        {
            invalid.Add("Kind");
        }
        else if (kind == CashAccountKind.Bank && bankShortName is null)
        {
            invalid.Add("Bank");
        }
        else if (kind == CashAccountKind.EWallet && !HasText(request.ProviderName, MaxNameLength))
        {
            invalid.Add("Provider name");
        }

        var number = SupplierPaymentRules.Trimmed(request.AccountNumber);
        if (number is null || number.Length > MaxAccountNumberLength || number.Any(char.IsWhiteSpace))
        {
            invalid.Add("Account number");
        }

        if (!HasText(request.HolderName, MaxNameLength))
        {
            invalid.Add("Holder name");
        }

        if (SupplierPaymentRules.Trimmed(request.Note)?.Length > MaxNoteLength)
        {
            invalid.Add("Note");
        }

        return invalid;
    }

    /// <summary>
    /// The fields of a voucher type. A user type is one of the categories a user may make: OtherIncome or TransferWithdrawal for a
    /// receipt, OtherExpense for a payment.
    /// </summary>
    public static List<string> InvalidType(SaveCashVoucherTypeRequest request)
    {
        var invalid = new List<string>();
        if (request.Direction is not { } direction || !Enum.IsDefined(direction))
        {
            invalid.Add("Direction");
        }

        if (request.Category is not { } category || !Enum.IsDefined(category) || (request.Direction is { } d && !UserCategories(d).Contains(category)))
        {
            invalid.Add("Category");
        }

        if (!HasText(request.Name, MaxNameLength))
        {
            invalid.Add("Name");
        }

        if (SupplierPaymentRules.Trimmed(request.Description)?.Length > MaxNoteLength)
        {
            invalid.Add("Description");
        }

        return invalid;
    }

    /// <summary>The categories a user may give a type of this direction.</summary>
    public static IReadOnlyList<CashCategory> UserCategories(CashDirection direction) => direction == CashDirection.Receipt
        ? [CashCategory.OtherIncome, CashCategory.TransferWithdrawal]
        : [CashCategory.OtherExpense];

    /// <summary>The fields of a party made on a voucher: a name, and the other texts within their columns.</summary>
    public static List<string> InvalidParty(CreateCashPartyRequest request)
    {
        var invalid = new List<string>();
        if (!HasText(request.Name, MaxNameLength))
        {
            invalid.Add("Name");
        }

        if (SupplierPaymentRules.Trimmed(request.PhoneNumber)?.Length > MaxPhoneLength)
        {
            invalid.Add("Phone number");
        }

        if (SupplierPaymentRules.Trimmed(request.Address)?.Length > MaxAddressLength)
        {
            invalid.Add("Address");
        }

        if (SupplierPaymentRules.Trimmed(request.Area)?.Length > MaxNameLength)
        {
            invalid.Add("Area");
        }

        if (SupplierPaymentRules.Trimmed(request.Ward)?.Length > MaxNameLength)
        {
            invalid.Add("Ward");
        }

        if (SupplierPaymentRules.Trimmed(request.Note)?.Length > MaxNoteLength)
        {
            invalid.Add("Note");
        }

        return invalid;
    }

    /// <summary>The fund of a supplier payment line: a bank transfer is the Bank fund, any other method the Cash fund.</summary>
    public static CashFund FundOfPayment(PaymentMethod method) => method == PaymentMethod.BankTransfer ? CashFund.Bank : CashFund.Cash;

    private static bool HasText(string? value, int maxLength) =>
        SupplierPaymentRules.Trimmed(value) is { } text && text.Length <= maxLength;
}
