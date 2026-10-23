using System.Net.Mail;

namespace OMTS.Application.Common.Validation;

/// <summary>Emails are stored trimmed and lower-case so uniqueness and login comparisons are exact.</summary>
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
        return MailAddress.TryCreate(trimmed, out var address) && address.Address == trimmed && trimmed.Contains('.', StringComparison.Ordinal)
            ? trimmed
            : null;
    }
}
