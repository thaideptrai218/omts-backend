using System.Security.Cryptography;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.JsonWebTokens;
using OMTS.Domain.Entities.Identity;
using OMTS.Infrastructure.Identity;

namespace OMTS.Api.IntegrationTests.Auth;

/// <summary>An access token expires after its lifetime, but never later than the session it belongs to.</summary>
public class AccessTokenExpiryTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 9, 8, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Token_lasts_the_configured_lifetime_while_the_session_has_more_time()
    {
        var service = CreateService();

        var token = service.CreateAccessToken(Account(), Guid.NewGuid(), Now.AddDays(3));

        Assert.Equal(Now.AddMinutes(15), token.ExpiresAt);
        Assert.Equal(Now.AddMinutes(15).ToUnixTimeSeconds(), ExpiryClaim(token.Token));
    }

    [Fact]
    public void Token_expires_with_the_session_when_that_comes_first()
    {
        var service = CreateService();
        var sessionEnd = Now.AddMinutes(4);

        var token = service.CreateAccessToken(Account(), Guid.NewGuid(), sessionEnd);

        Assert.Equal(sessionEnd, token.ExpiresAt);
        Assert.Equal(sessionEnd.ToUnixTimeSeconds(), ExpiryClaim(token.Token));
    }

    private static long ExpiryClaim(string token) => (long)new JsonWebToken(token).GetPayloadValue<int>("exp");

    private static Account Account() => new() { Username = "expiry", DisplayName = "expiry" };

    private static JwtTokenService CreateService() => new(
        Options.Create(new JwtOptions { SigningKey = Convert.ToBase64String(RandomNumberGenerator.GetBytes(64)) }),
        new StillClock(Now));

    private sealed class StillClock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
