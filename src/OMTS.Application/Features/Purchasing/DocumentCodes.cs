using System.Globalization;

namespace OMTS.Application.Features.Purchasing;

/// <summary>
/// Document codes. Purchase receipts follow GBR-04: PN000123, one cooperative-wide sequence; the seed lot of a Seed line is
/// the receipt code and the line number (PN000123-1). Returns keep their daily code PRT-20261022-001 until PURCHASE-02.
/// </summary>
public static class DocumentCodes
{
    public const string ReceiptPrefix = "PN";
    public const string ReturnPrefix = "PRT";

    /// <summary>PN followed by at least six digits: PN000001…PN999999, then PN1000000.</summary>
    public static string ReceiptCode(int number) => $"{ReceiptPrefix}{number.ToString("000000", CultureInfo.InvariantCulture)}";

    /// <summary>The number of a receipt code of this sequence, or null for another code (e.g. an old PR-yyyyMMdd-nnn).</summary>
    public static int? ReceiptNumber(string? code) =>
        code is { Length: > 2 } && code.StartsWith(ReceiptPrefix, StringComparison.Ordinal) && code[2..].All(char.IsAsciiDigit)
            && int.TryParse(code[2..], NumberStyles.None, CultureInfo.InvariantCulture, out var number)
            ? number
            : null;

    /// <summary>The seed lot code of a Seed line (GBR-04), written on the seed bags.</summary>
    public static string SeedLotCode(string receiptCode, int lineNumber) => $"{receiptCode}-{lineNumber.ToString(CultureInfo.InvariantCulture)}";

    public static string PrefixFor(string kind, DateOnly day) => $"{kind}-{day:yyyyMMdd}-";

    /// <param name="used">Codes of that day already used.</param>
    /// <param name="attempt">0 first; a code taken by a parallel save is retried with the next number.</param>
    public static string Next(string prefix, int used, int attempt = 0) => $"{prefix}{used + 1 + attempt:000}";
}
