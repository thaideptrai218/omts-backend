using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;
using OMTS.Application.Common.Constants;
using OMTS.Application.Common.Interfaces.Identity;
using OMTS.Application.Common.Models;
using OMTS.Domain.Entities.Identity;
using OMTS.Domain.Enums;

namespace OMTS.Infrastructure.Identity;

internal sealed class JwtTokenService(IOptions<JwtOptions> options, TimeProvider timeProvider) : ITokenService
{
    private readonly JsonWebTokenHandler _handler = new();

    public AccessToken CreateAccessToken(Account account, Guid sessionId)
    {
        var jwt = options.Value;
        var now = timeProvider.GetUtcNow();
        var expiresAt = now + jwt.AccessTokenLifetime;

        // Only identity goes in the token; zone–role permissions are read from the database so role
        // edits apply immediately (BR-19).
        var descriptor = new SecurityTokenDescriptor
        {
            Issuer = jwt.Issuer,
            Audience = jwt.Audience,
            IssuedAt = now.UtcDateTime,
            NotBefore = now.UtcDateTime,
            Expires = expiresAt.UtcDateTime,
            SigningCredentials = new SigningCredentials(jwt.CreateSigningKey(), SecurityAlgorithms.HmacSha256),
            Claims = new Dictionary<string, object>
            {
                [OmtsClaimTypes.Subject] = account.Id.ToString(),
                [OmtsClaimTypes.SessionId] = sessionId.ToString(),
                [OmtsClaimTypes.TokenId] = Guid.NewGuid().ToString(),
                [OmtsClaimTypes.Name] = account.Username,
                [OmtsClaimTypes.SubjectType] = account.SubjectType.ToString(),
            },
        };

        return new AccessToken(_handler.CreateToken(descriptor), expiresAt);
    }

    // "{sessionId}.{512 random bits}.{MAC}": the session id is not a secret (it is in every access token) and
    // the random part is. The MAC proves the server issued the token, so a made-up token naming someone's
    // session cannot end it through the reuse check (only a token that was really issued counts as replayed).
    public string CreateRefreshToken(Guid sessionId)
    {
        var body = $"{sessionId:N}.{Base64UrlEncoder.Encode(RandomNumberGenerator.GetBytes(64))}";
        return $"{body}.{Mac(body)}";
    }

    public Guid? ReadSessionId(string refreshToken)
    {
        var separator = refreshToken.IndexOf('.');
        var macSeparator = refreshToken.LastIndexOf('.');
        if (separator <= 0 || macSeparator <= separator
            || !CryptographicOperations.FixedTimeEquals(
                Encoding.ASCII.GetBytes(Mac(refreshToken[..macSeparator])),
                Encoding.ASCII.GetBytes(refreshToken[(macSeparator + 1)..])))
        {
            return null;
        }

        return Guid.TryParseExact(refreshToken[..separator], "N", out var sessionId) ? sessionId : null;
    }

    // Keyed with a key derived from the JWT signing key, so the two uses never share a key.
    private string Mac(string body)
    {
        var key = HMACSHA256.HashData(options.Value.CreateSigningKey().Key, "omts-refresh-token-mac"u8);
        return Base64UrlEncoder.Encode(HMACSHA256.HashData(key, Encoding.UTF8.GetBytes(body)));
    }

    // Refresh tokens are 512-bit random values, so a fast hash is enough (unlike passwords).
    public string HashRefreshToken(string refreshToken) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(refreshToken)));

    public SessionLifetime GetSessionLifetime(ClientType clientType)
    {
        var jwt = options.Value;
        return clientType == ClientType.Mobile
            ? new SessionLifetime(jwt.MobileRefreshTokenLifetime, jwt.MobileSessionMaxLifetime)
            : new SessionLifetime(jwt.WebRefreshTokenLifetime, jwt.WebSessionMaxLifetime);
    }
}
