using System.Net.Mail;

namespace OMTS.Application.Common.Validation;

/// <summary>
/// Emails are stored trimmed and lower-case so uniqueness and login comparisons are exact. A quoted local part and a final
/// dot are refused: they are other spellings of one mailbox, which would pass uniqueness and the limits on codes as new
/// addresses (BR-ACCOUNT-02, BR-ACCOUNT-09).
/// </summary>
public static class EmailAddresses
{
    public const int MaxLength = 256;

    /// <summary>The normalised email, or null when it is not a valid address.</summary>
    public static string? Normalize(string? input)
    {
        if (string.IsNullOrWhiteSpace(input) || input.Length > MaxLength)
        {
            return null;
        }

        var trimmed = input.Trim().ToLowerInvariant();
        if (trimmed.Contains('"', StringComparison.Ordinal) || trimmed.EndsWith('.'))
        {
            return null;
        }

        return MailAddress.TryCreate(trimmed, out var address) && address.Address == trimmed && trimmed.Contains('.', StringComparison.Ordinal)
            ? trimmed
            : null;
    }
}
