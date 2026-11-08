using System.Text.RegularExpressions;
using OMTS.Application.Common.Interfaces.Payments;
using OMTS.Application.Features.Sales.Invoices;

namespace OMTS.Application.Features.Sales.Payments;

/// <summary>Why a reported transfer is not recorded (GFR-08: only transfers to the receiving bank account made with the VietQR code of an invoice are).</summary>
public enum BankTransferIgnoredReason
{
    /// <summary>A reference, an amount in whole VND above 0 or the account is missing.</summary>
    Invalid,

    /// <summary>Money out of the account.</summary>
    Outgoing,

    /// <summary>Another account, or an account no longer the receiving bank account (BR-DEBT-01: the Accountant records it).</summary>
    NotTheReceivingAccount,

    /// <summary>A transfer made without the VietQR code of an invoice: the Accountant records it (BR-DEBT-03).</summary>
    NoInvoiceCode,
}

/// <summary>GFR-08 as pure rules: which transfers are recorded and the invoice code their content carries.</summary>
public static partial class BankTransferRules
{
    public const int MaxBankReferenceLength = 100;

    /// <summary>
    /// The invoice code of the transfer content: HD and at least six digits (<see cref="SalesDocumentCodes.InvoiceCode"/>). Bank apps add their own words and may change the case
    /// or put a separator after HD ("MBVCB.1234.hd-000123.CT tu ..."), so these are accepted; letters right before HD are not (another word). The first code found counts.
    /// </summary>
    public static string? InvoiceCodeIn(string? content)
    {
        if (string.IsNullOrWhiteSpace(content))
        {
            return null;
        }

        var match = InvoiceCodePattern().Match(content);
        return match.Success ? $"HD{match.Groups["digits"].Value}" : null;
    }

    /// <summary>The reason a transfer is not recorded, or null when it is (before the invoice code is looked up).</summary>
    public static BankTransferIgnoredReason? Ignored(BankTransferNotice notice, string? receivingAccountNumber)
    {
        if (string.IsNullOrWhiteSpace(notice.BankReference) || notice.BankReference.Trim().Length > MaxBankReferenceLength
            || !InvoiceMoney.IsWholeAmount(notice.Amount) || notice.Amount <= 0m || string.IsNullOrWhiteSpace(notice.AccountNumber))
        {
            return BankTransferIgnoredReason.Invalid;
        }

        if (notice.Direction != BankTransferDirection.In)
        {
            return BankTransferIgnoredReason.Outgoing;
        }

        if (receivingAccountNumber is null || !SameAccount(notice.AccountNumber, receivingAccountNumber))
        {
            return BankTransferIgnoredReason.NotTheReceivingAccount;
        }

        return InvoiceCodeIn(notice.Content) is null ? BankTransferIgnoredReason.NoInvoiceCode : null;
    }

    /// <summary>Account numbers compared as banks print them: spaces ignored, letters in any case.</summary>
    public static bool SameAccount(string reported, string receiving) =>
        string.Equals(Plain(reported), Plain(receiving), StringComparison.OrdinalIgnoreCase);

    private static string Plain(string accountNumber) => string.Concat(accountNumber.Where(c => !char.IsWhiteSpace(c)));

    [GeneratedRegex(@"(?<![A-Za-z])HD[\s\-_.]?(?<digits>\d{6,12})(?!\d)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 1000)]
    private static partial Regex InvoiceCodePattern();
}
