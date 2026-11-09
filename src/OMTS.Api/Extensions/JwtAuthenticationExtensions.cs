using System.Security.Claims;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;
using OMTS.Application.Common.Constants;
using OMTS.Application.Common.Interfaces.Identity;
using OMTS.Application.Features.Auth;
using OMTS.Infrastructure.Identity;

namespace OMTS.Api.Extensions;

public static class JwtAuthenticationExtensions
{
    public static IServiceCollection AddJwtAuthentication(this IServiceCollection services)
    {
        services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme).AddJwtBearer();

        // Configured from JwtOptions lazily, so the app (and /health) starts without a signing key;
        // tokens are simply rejected until one is set.
        services.AddOptions<JwtBearerOptions>(JwtBearerDefaults.AuthenticationScheme)
            .Configure<IOptions<JwtOptions>>((bearer, jwtOptions) =>
            {
                var jwt = jwtOptions.Value;
                bearer.MapInboundClaims = false;
                bearer.TokenValidationParameters = new TokenValidationParameters
                {
                    ValidIssuer = jwt.Issuer,
                    ValidAudience = jwt.Audience,
                    IssuerSigningKey = jwt.HasSigningKey ? jwt.CreateSigningKey() : null,
                    ValidateIssuerSigningKey = true,
                    ValidAlgorithms = [SecurityAlgorithms.HmacSha256],
                    // The default 5 minutes would stretch the 15-minute access session (NFR-01).
                    ClockSkew = TimeSpan.FromSeconds(30),
                    NameClaimType = OmtsClaimTypes.Name,
                    RoleClaimType = OmtsClaimTypes.SubjectType,
                };
                bearer.Events = new JwtBearerEvents { OnTokenValidated = RejectRevokedSessionAsync };
            });

        services.AddAuthorization();
        return services;
    }

    // A valid signature is not enough: logout, password change or locking the account revokes the
    // session, and its access tokens must stop working immediately (NFR-01).
    // The revocation store answers first. It lists revoked sessions only (the access token's own expiry covers the
    // session's absolute end), so an account locked outside the application, without its sessions being revoked, stays
    // accepted for up to the access token's lifetime. Only when the store cannot answer (unreachable, cold, not
    // configured) does the request go to the database, which also checks the account status.
    private static async Task RejectRevokedSessionAsync(TokenValidatedContext context)
    {
        var principal = context.Principal;
        if (!Guid.TryParse(principal?.FindFirstValue(OmtsClaimTypes.Subject), out var accountId)
            || !Guid.TryParse(principal?.FindFirstValue(OmtsClaimTypes.SessionId), out var sessionId))
        {
            context.Fail("Token has no session.");
            return;
        }

        var services = context.HttpContext.RequestServices;
        switch (await services.GetRequiredService<ISessionRevocationStore>().GetStatusAsync(sessionId))
        {
            case SessionRevocationStatus.Revoked:
                context.Fail("Session is no longer active.");
                return;
            case SessionRevocationStatus.NotRevoked:
                return;
        }

        var authService = services.GetRequiredService<AuthService>();
        if (!await authService.IsSessionActiveAsync(accountId, sessionId, context.HttpContext.RequestAborted))
        {
            context.Fail("Session is no longer active.");
        }
    }
}
