using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using OMTS.Application.Authentication;
using OMTS.Application.Common.Interfaces;

namespace OMTS.Api.Controllers;

[ApiController]
[Route("api/auth")]
public sealed class AuthController(AuthService authService, ICurrentUser currentUser) : ControllerBase
{
    /// <summary>UC02 – log in with username, verified phone number or verified email.</summary>
    [HttpPost("login")]
    [AllowAnonymous]
    [ProducesResponseType<AuthTokens>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status403Forbidden)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status423Locked)]
    public async Task<IActionResult> Login(LoginRequest request, CancellationToken cancellationToken)
    {
        var client = new ClientInfo(Request.Headers.UserAgent.ToString(), HttpContext.Connection.RemoteIpAddress?.ToString());
        return ToResponse(await authService.LoginAsync(request, client, cancellationToken));
    }

    /// <summary>Exchanges a refresh token for new tokens; the old refresh token stops working.</summary>
    [HttpPost("refresh")]
    [AllowAnonymous]
    [ProducesResponseType<AuthTokens>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status401Unauthorized)]
    public async Task<IActionResult> Refresh(RefreshRequest request, CancellationToken cancellationToken) =>
        ToResponse(await authService.RefreshAsync(request, cancellationToken));

    /// <summary>UC07 – revokes the current session; its access and refresh tokens stop working.</summary>
    [HttpPost("logout")]
    [Authorize]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    public async Task<IActionResult> Logout(CancellationToken cancellationToken)
    {
        await authService.LogoutAsync(currentUser.AccountId!.Value, currentUser.SessionId!.Value, cancellationToken);
        return NoContent();
    }

    /// <summary>Identity of the caller, read from the access token.</summary>
    [HttpGet("me")]
    [Authorize]
    public IActionResult Me() => Ok(new
    {
        accountId = currentUser.AccountId,
        sessionId = currentUser.SessionId,
        username = User.Identity?.Name,
        subjectType = User.FindFirst(OmtsClaimTypes.SubjectType)?.Value,
    });

    private IActionResult ToResponse(AuthResult result)
    {
        if (result.Tokens is not null)
        {
            return Ok(result.Tokens);
        }

        // The SRS message code lets clients show the Vietnamese text for each case.
        var (status, code, detail) = result.Error switch
        {
            AuthError.InvalidCredentials => (StatusCodes.Status401Unauthorized, "MSG12", "Wrong identifier or password."),
            AuthError.TemporarilyLocked => (StatusCodes.Status423Locked, "MSG69", "Too many failed logins; try again in 15 minutes."),
            AuthError.AccessDenied => (StatusCodes.Status403Forbidden, "MSG13", "Account suspended or no longer authorized."),
            AuthError.TemporaryPasswordExpired => (StatusCodes.Status403Forbidden, "MSG68", "Temporary password expired; ask for a new one."),
            _ => (StatusCodes.Status401Unauthorized, "MSG19", "Session expired; log in again."),
        };

        var problem = new ProblemDetails { Status = status, Title = code, Detail = detail };
        problem.Extensions["code"] = code;
        return StatusCode(status, problem);
    }
}
