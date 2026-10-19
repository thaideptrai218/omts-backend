using System.Text;
using Microsoft.IdentityModel.Tokens;

namespace OMTS.Infrastructure.Authentication;

/// <summary>
/// "Jwt" configuration section. Lifetimes default to NFR-01; the signing key is a secret and is set
/// through user-secrets / environment variables, never in appsettings.json.
/// </summary>
public sealed class JwtOptions
{
    public const string SectionName = "Jwt";
    public const int MinimumSigningKeyBytes = 32;

    public string Issuer { get; set; } = "omts-api";
    public string Audience { get; set; } = "omts-clients";
    public string SigningKey { get; set; } = string.Empty;

    public TimeSpan AccessTokenLifetime { get; set; } = TimeSpan.FromMinutes(15);
    public TimeSpan WebRefreshTokenLifetime { get; set; } = TimeSpan.FromDays(7);
    public TimeSpan WebSessionMaxLifetime { get; set; } = TimeSpan.FromDays(30);
    public TimeSpan MobileRefreshTokenLifetime { get; set; } = TimeSpan.FromDays(30);
    public TimeSpan MobileSessionMaxLifetime { get; set; } = TimeSpan.FromDays(90);

    public bool HasSigningKey => Encoding.UTF8.GetByteCount(SigningKey) >= MinimumSigningKeyBytes;

    public SymmetricSecurityKey CreateSigningKey()
    {
        if (!HasSigningKey)
        {
            throw new InvalidOperationException(
                $"'{SectionName}:SigningKey' must be at least {MinimumSigningKeyBytes} bytes. " +
                $"Run: dotnet user-secrets set \"{SectionName}:SigningKey\" \"<random 64+ chars>\" --project src/OMTS.Api");
        }

        return new SymmetricSecurityKey(Encoding.UTF8.GetBytes(SigningKey));
    }
}
