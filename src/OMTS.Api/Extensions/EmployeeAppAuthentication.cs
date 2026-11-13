using System.Security.Claims;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.IdentityModel.Tokens;
using OMTS.Application.Common.Constants;
using OMTS.Application.Features.EmployeeApp;
using OMTS.Infrastructure.Identity;

namespace OMTS.Api.Extensions;

/// <summary>
/// The authentication scheme of the employee app (BR-LABOR-02): a second kind of caller, never an account. It is opt-in: only an
/// endpoint that names it (<c>[Authorize(AuthenticationSchemes = EmployeeAppAuthentication.Scheme)]</c>) accepts its tokens, and the
/// default scheme every staff endpoint uses refuses them. Three things tell the two kinds of token apart, so one wrong setting is not
/// enough to mix them: the audience, the signing key (the employee one is derived from the staff one with its own label) and the
/// claims (an employee token has no <c>sub</c> and no <c>sid</c>).
/// </summary>
public static class EmployeeAppAuthentication
{
    public const string Scheme = "EmployeeApp";

    public static void Configure(JwtBearerOptions bearer, JwtOptions jwt)
    {
        bearer.MapInboundClaims = false;
        bearer.TokenValidationParameters = new TokenValidationParameters
        {
            ValidIssuer = jwt.Issuer,
            ValidAudience = jwt.EmployeeAudience,
            IssuerSigningKey = jwt.HasSigningKey ? jwt.CreateEmployeeSigningKey() : null,
            ValidateIssuerSigningKey = true,
            ValidAlgorithms = [SecurityAlgorithms.HmacSha256],
            ClockSkew = TimeSpan.FromSeconds(30),
            NameClaimType = OmtsClaimTypes.Name,
            RoleClaimType = OmtsClaimTypes.SubjectType,
        };
        bearer.Events = new JwtBearerEvents { OnTokenValidated = RejectInactiveSessionAsync };
    }

    // A valid signature is not enough: the session may be revoked, its device unlinked, or the employee no longer Employed today, and
    // the token must stop working at once (GFR-02, BR-LABOR-02). Every request asks the database; there is no revocation store for employees.
    private static async Task RejectInactiveSessionAsync(TokenValidatedContext context)
    {
        var principal = context.Principal;
        if (principal is null
            || principal.FindFirstValue(OmtsClaimTypes.SubjectType) != OmtsClaimTypes.EmployeeSubjectType
            || principal.HasClaim(c => c.Type is OmtsClaimTypes.Subject or OmtsClaimTypes.SessionId)
            || !Guid.TryParse(principal.FindFirstValue(OmtsClaimTypes.EmployeeId), out var employeeId)
            || !Guid.TryParse(principal.FindFirstValue(OmtsClaimTypes.EmployeeSessionId), out var sessionId)
            || !Guid.TryParse(principal.FindFirstValue(OmtsClaimTypes.EmployeeDeviceId), out var deviceId))
        {
            context.Fail("Token is not an employee app token.");
            return;
        }

        var sessions = context.HttpContext.RequestServices.GetRequiredService<EmployeeAppSessionService>();
        if (!await sessions.IsSessionActiveAsync(employeeId, sessionId, deviceId, context.HttpContext.RequestAborted))
        {
            context.Fail("Session is no longer active.");
        }
    }
}
