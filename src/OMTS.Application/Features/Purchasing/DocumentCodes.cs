namespace OMTS.Application.Features.Purchasing;

/// <summary>Daily document codes such as PR-20261022-001 (receipts) and PRT-20261022-001 (returns).</summary>
public static class DocumentCodes
{
    public const string ReceiptPrefix = "PR";
    public const string ReturnPrefix = "PRT";

    public static string PrefixFor(string kind, DateOnly day) => $"{kind}-{day:yyyyMMdd}-";

    /// <param name="used">Codes of that day already used.</param>
    /// <param name="attempt">0 first; a code taken by a parallel save is retried with the next number.</param>
    public static string Next(string prefix, int used, int attempt = 0) => $"{prefix}{used + 1 + attempt:000}";
}
