using System.Globalization;
using System.Text.RegularExpressions;

namespace OMTS.Application.Features.Purchasing;

/// <summary>
/// Document codes. Purchase receipts follow GBR-04: PN000123, one cooperative-wide sequence; the seed lot of a Seed line is
/// the receipt code and the line number (PN000123-1). Supplier payments are TT000123 and purchase returns TH000123, in one
/// sequence each. A receipt, a return and a payment may instead get a code the user types (<see cref="Typed"/>).
/// </summary>
public static partial class DocumentCodes
{
    /// <summary>The longest code a user may type; the column holds more, and the seed lot code adds "-" and the line number.</summary>
    public const int MaxTypedLength = 30;

    public const string ReceiptPrefix = "PN";
    public const string PaymentPrefix = "TT";
    public const string ReturnPrefix = "TH";
    public const string SupplierPrefix = "NCC";

    /// <summary>
    /// PN followed by at least six digits: PN000001…PN999999, then PN1000000. The number comes from the PN document
    /// sequence (<c>IDocumentNumberAllocator</c>), so a code is never given twice, even after its Draft is removed.
    /// </summary>
    public static string ReceiptCode(long number) => $"{ReceiptPrefix}{number.ToString("000000", CultureInfo.InvariantCulture)}";

    /// <summary>TT followed by at least six digits; the number comes from the TT document sequence (BR-PURCHASE-08: the system gives each payment a code).</summary>
    public static string PaymentCode(long number) => $"{PaymentPrefix}{number.ToString("000000", CultureInfo.InvariantCulture)}";

    /// <summary>TH followed by at least six digits; the number comes from the TH document sequence (PURCHASE-02 step 6: saved "with its code").</summary>
    public static string ReturnCode(long number) => $"{ReturnPrefix}{number.ToString("000000", CultureInfo.InvariantCulture)}";

    /// <summary>NCC followed by at least six digits; the number comes from the NCC document sequence.</summary>
    public static string SupplierCode(long number) => $"{SupplierPrefix}{number.ToString("000000", CultureInfo.InvariantCulture)}";

    /// <summary>The seed lot code of a Seed line (GBR-04), written on the seed bags.</summary>
    public static string SeedLotCode(string receiptCode, int lineNumber) => $"{receiptCode}-{lineNumber.ToString(CultureInfo.InvariantCulture)}";

    /// <summary>
    /// A code typed by the user, trimmed: null when left empty (the system then gives one). Valid when it has 1 to
    /// <see cref="MaxTypedLength"/> characters, each an ASCII letter, a digit, "-", "_" or "."; it is kept as typed (case included).
    /// </summary>
    public static string? Typed(string? input) => string.IsNullOrWhiteSpace(input) ? null : input.Trim();

    /// <summary>Whether <paramref name="input"/> is empty (the system gives the code) or a valid typed code (<see cref="Typed"/>).</summary>
    public static bool TypedAllowed(string? input) => Typed(input) is not { } code || TypedPattern().IsMatch(code);

    /// <summary>
    /// Whether <paramref name="input"/> may be sent for a document whose code can no longer change: empty, or the stored code
    /// (letter case apart, as codes are unique regardless of case).
    /// </summary>
    public static bool SameOrEmpty(string? input, string storedCode) =>
        Typed(input) is not { } code || string.Equals(code, storedCode, StringComparison.OrdinalIgnoreCase);

    [GeneratedRegex(@"^[A-Za-z0-9_.\-]{1,30}$", RegexOptions.CultureInvariant)]
    private static partial Regex TypedPattern();
}
