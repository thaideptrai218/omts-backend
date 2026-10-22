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

    // "{sessionId}.{512 random bits}": the session id is not a secret, the random part is.
    public string CreateRefreshToken(Guid sessionId) =>
        $"{sessionId:N}.{Base64UrlEncoder.Encode(RandomNumberGenerator.GetBytes(64))}";

    public Guid? ReadSessionId(string refreshToken)
    {
        var separator = refreshToken.IndexOf('.');
        return separator > 0 && Guid.TryParseExact(refreshToken[..separator], "N", out var sessionId) ? sessionId : null;
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
