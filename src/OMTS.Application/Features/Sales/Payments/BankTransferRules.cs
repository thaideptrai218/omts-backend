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

    /// <summary>No transfer of the cooperative is older; a time before it is a malformed field.</summary>
    public static readonly DateTimeOffset EarliestTransferTime = new(2000, 1, 1, 0, 0, 0, TimeSpan.Zero);

    /// <summary>Bank clocks run a little ahead at most; a time further in the future is a malformed field.</summary>
    public static readonly TimeSpan MaxClockAhead = TimeSpan.FromDays(1);

    /// <summary>
    /// Whether the content carries the code of a payment (a PT code or one the user typed): the code as a word of its own, not part of a longer number or word, in any letter case.
    /// "MBVCB.1234.PT000123.CT tu" carries PT000123; "PT0001234" does not.
    /// </summary>
    public static bool ContainsPaymentCode(string? content, string paymentCode)
    {
        if (string.IsNullOrWhiteSpace(content) || string.IsNullOrWhiteSpace(paymentCode))
        {
            return false;
        }

        var code = paymentCode.Trim();
        var after = char.IsAsciiDigit(code[^1]) ? @"(?!\d)" : "(?![A-Za-z0-9])";
        return Regex.IsMatch(
            content, $"(?<![A-Za-z0-9]){Regex.Escape(code)}{after}", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(1000));
    }

    /// <summary>The reason a notice is not a transfer in at all (a field malformed, or money out), or null; whatever the account and the content.</summary>
    /// <param name="now">The time of the check: a transfer time more than <see cref="MaxClockAhead"/> after it is <see cref="BankTransferIgnoredReason.Invalid"/>.</param>
    public static BankTransferIgnoredReason? NotATransfer(BankTransferNotice notice, DateTimeOffset? now = null)
    {
        if (string.IsNullOrWhiteSpace(notice.BankReference) || notice.BankReference.Trim().Length > MaxBankReferenceLength
            || !InvoiceMoney.IsWholeAmount(notice.Amount) || notice.Amount <= 0m || string.IsNullOrWhiteSpace(notice.AccountNumber)
            || !IsTransferTime(notice.OccurredAt, now))
        {
            return BankTransferIgnoredReason.Invalid;
        }

        return notice.Direction != BankTransferDirection.In ? BankTransferIgnoredReason.Outgoing : null;
    }

    /// <summary>The reason a transfer is not recorded as one of an invoice, or null when it is (before the invoice code is looked up).</summary>
    /// <param name="now">The time of the check: a transfer time more than <see cref="MaxClockAhead"/> after it is <see cref="BankTransferIgnoredReason.Invalid"/>.</param>
    public static BankTransferIgnoredReason? Ignored(BankTransferNotice notice, string? receivingAccountNumber, DateTimeOffset? now = null)
    {
        if (NotATransfer(notice, now) is { } notTransfer)
        {
            return notTransfer;
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

    /// <summary>A time the business clock (UTC+7) can hold and a transfer can have: never the end of the calendar, which no date can be made of (API-13 must not fail on it).</summary>
    private static bool IsTransferTime(DateTimeOffset occurredAt, DateTimeOffset? now) =>
        occurredAt >= EarliestTransferTime
        && occurredAt.UtcDateTime < DateTime.MaxValue.AddDays(-2)
        && (now is null || occurredAt <= now.Value + MaxClockAhead);

    private static string Plain(string accountNumber) => string.Concat(accountNumber.Where(c => !char.IsWhiteSpace(c)));

    [GeneratedRegex(@"(?<![A-Za-z])HD[\s\-_.]?(?<digits>\d{6,12})(?!\d)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 1000)]
    private static partial Regex InvoiceCodePattern();
}
