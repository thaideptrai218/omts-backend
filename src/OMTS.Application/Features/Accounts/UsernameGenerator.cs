using System.Security.Cryptography;
using System.Text.RegularExpressions;
using OMTS.Application.Common.Interfaces.Persistence;

namespace OMTS.Application.Features.Accounts;

/// <summary>
/// Username rules: 3–50 characters a–z, 0–9, dot, dash or underscore, with at least one letter. A username
/// can then never equal a phone number (digits only) or an email (has "@"), so the login identifier is
/// never ambiguous between two accounts.
/// </summary>
public sealed partial class UsernameGenerator(IAccountRepository accounts)
{
    public const int MaxLength = 50;

    public static string? Normalize(string? input)
    {
        var value = input?.Trim().ToLowerInvariant();
        return value is not null && UsernamePattern().IsMatch(value) ? value : null;
    }

    /// <summary>"tr" + 8 random digits, unique.</summary>
    public async Task<string> NewTraderUsernameAsync(CancellationToken cancellationToken)
    {
        while (true)
        {
            var username = $"tr{RandomNumberGenerator.GetInt32(0, 100_000_000):D8}";
            if (!await accounts.UsernameExistsAsync(username, null, cancellationToken))
            {
                return username;
            }
        }
    }

    [GeneratedRegex(@"\A(?=[a-z0-9._-]*[a-z])[a-z0-9._-]{3,50}\z")]
    private static partial Regex UsernamePattern();
}
