using System.Text.RegularExpressions;
using OMTS.Application.Common.Interfaces.Payments;

namespace OMTS.Application.Features.ReceivingBankAccounts;

/// <summary>The checks of BR-DEBT-01: the bank from the list, the VietQR format of the number, a holder name.</summary>
public static partial class ReceivingBankAccountRules
{
    public const string BankField = "Bank";
    public const string AccountNumberField = "Account number";
    public const string AccountHolderField = "Account holder name";

    /// <summary>The length of the column; a real holder name is far shorter.</summary>
    public const int MaxAccountHolderName = 200;

    /// <summary>
    /// "The format that VietQR accepts": 1 to 19 letters or digits, the beneficiary number of NAPAS, with no spaces. The SRS
    /// names no rule of its own (PM question). Case is kept: the bank decides whether it matters.
    /// </summary>
    public static bool IsValidAccountNumber(string? number) => number is not null && AccountNumberPattern().IsMatch(number);

    /// <summary>The normalised account (holder name in capitals, as banks print it) and every invalid field, in form order.</summary>
    public static (ReceivingAccountToAccept Account, IReadOnlyList<string> Invalid) Read(SaveReceivingBankAccountRequest request)
    {
        var invalid = new List<string>();

        var bank = BankCatalog.FindByBin(request.BankBin?.Trim());
        if (bank is null)
        {
            invalid.Add(BankField);
        }

        var number = request.AccountNumber?.Trim() ?? string.Empty;
        if (!IsValidAccountNumber(number))
        {
            invalid.Add(AccountNumberField);
        }

        var holder = request.AccountHolderName?.Trim() ?? string.Empty;
        if (holder.Length is 0 or > MaxAccountHolderName || holder.Any(char.IsControl))
        {
            invalid.Add(AccountHolderField);
        }

        return (new ReceivingAccountToAccept(bank?.Bin ?? string.Empty, bank?.ShortName ?? string.Empty, number, holder.ToUpperInvariant()), invalid);
    }

    [GeneratedRegex(@"\A[0-9A-Za-z]{1,19}\z")]
    private static partial Regex AccountNumberPattern();
}
