using System.Globalization;
using System.Text;

namespace OMTS.Application.Features.ReceivingBankAccounts;

/// <summary>
/// The text of the VietQR code of a sales invoice (EMVCo QR, NAPAS account transfer); the client draws the image. Made
/// again each time the code is shown, for the amount still to pay and the current account (BR-DEBT-01, BR-SALE-07). No
/// network. The name of the account holder is not part of it: the bank app shows the name the bank knows.
/// </summary>
public static class VietQrPayload
{
    private const string NapasId = "A000000727";
    private const string TransferToAccount = "QRIBFTTA";
    private const string DynamicCode = "12";
    private const string CurrencyVnd = "704";
    private const string CountryVietnam = "VN";

    /// <summary>EMVCo: a transaction amount holds at most 13 characters.</summary>
    private const long MaxAmount = 9_999_999_999_999;

    /// <summary>EMVCo: the purpose of the transaction holds at most 25 characters.</summary>
    public const int MaxContentLength = 25;

    /// <param name="bankBin">The 6 digits of the bank.</param>
    /// <param name="accountNumber">In the format of <see cref="ReceivingBankAccountRules.IsValidAccountNumber"/>.</param>
    /// <param name="amount">Whole VND, above zero.</param>
    /// <param name="content">The transfer content, the invoice code. See <see cref="ContentOf"/>.</param>
    /// <exception cref="ArgumentException">A value cannot be put in a code: a bug of the caller, which has checked them.</exception>
    public static string Build(string bankBin, string accountNumber, long amount, string content)
    {
        if (bankBin.Length != 6 || !bankBin.All(char.IsAsciiDigit))
        {
            throw new ArgumentException("A bank BIN has 6 digits.", nameof(bankBin));
        }

        if (!ReceivingBankAccountRules.IsValidAccountNumber(accountNumber))
        {
            throw new ArgumentException("The account number does not have the VietQR format.", nameof(accountNumber));
        }

        if (amount is <= 0 or > MaxAmount)
        {
            throw new ArgumentOutOfRangeException(nameof(amount), "The amount must be above zero and fit 13 digits.");
        }

        var cleanContent = ContentOf(content);
        if (cleanContent.Length == 0)
        {
            throw new ArgumentException("The transfer content is empty.", nameof(content));
        }

        var beneficiary = EmvQrEncoding.Field("00", bankBin) + EmvQrEncoding.Field("01", accountNumber);
        var merchantAccount = EmvQrEncoding.Field("00", NapasId) + EmvQrEncoding.Field("01", beneficiary) + EmvQrEncoding.Field("02", TransferToAccount);
        return EmvQrEncoding.WithCrc(
            EmvQrEncoding.Field("00", "01")
            + EmvQrEncoding.Field("01", DynamicCode)
            + EmvQrEncoding.Field("38", merchantAccount)
            + EmvQrEncoding.Field("53", CurrencyVnd)
            + EmvQrEncoding.Field("54", amount.ToString(CultureInfo.InvariantCulture))
            + EmvQrEncoding.Field("58", CountryVietnam)
            + EmvQrEncoding.Field("62", EmvQrEncoding.Field("08", cleanContent)));
    }

    /// <summary>
    /// The content as a code holds it: ASCII without Vietnamese marks ("Hàu Vân Đồn" → "Hau Van Don"), because a length
    /// counts characters and readers that count bytes would misread any other letter; single spaces; at most
    /// <see cref="MaxContentLength"/> characters. The invoice code at its start survives the cut.
    /// </summary>
    public static string ContentOf(string content)
    {
        var plain = new StringBuilder(content.Length);
        foreach (var letter in content.Normalize(NormalizationForm.FormD))
        {
            var ascii = letter switch { 'đ' => 'd', 'Đ' => 'D', _ => letter };
            if (char.IsWhiteSpace(ascii))
            {
                plain.Append(' ');
            }
            else if (ascii is >= '!' and <= '~')
            {
                plain.Append(ascii);
            }
        }

        var words = plain.ToString().Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var joined = string.Join(' ', words);
        return joined.Length <= MaxContentLength ? joined : joined[..MaxContentLength].TrimEnd();
    }
}
