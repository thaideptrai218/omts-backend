using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using OMTS.Api.Common;
using OMTS.Api.Extensions;
using OMTS.Api.Filters;
using OMTS.Application.Common.Interfaces;
using OMTS.Application.Common.Messages;
using OMTS.Application.Features.Auth;
using OMTS.Application.Features.Auth.Dtos;

namespace OMTS.Api.Controllers;

[ApiController]
[Route("api/auth")]
public sealed class AuthController(AuthService authService, ICurrentUser currentUser) : ControllerBase
{
    /// <summary>UC02 – log in with username, verified phone number or verified email.</summary>
    [EnableRateLimiting(RateLimitingExtensions.Logins)]
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
    [AllowWhenPasswordChangeRequired]
    [AllowWhenPhoneUnverified]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    public async Task<IActionResult> Logout(CancellationToken cancellationToken)
    {
        await authService.LogoutAsync(currentUser.AccountId!.Value, currentUser.SessionId!.Value, cancellationToken);
        return NoContent();
    }

    private IActionResult ToResponse(AuthResult result)
    {
        if (result.Tokens is not null)
        {
            return Ok(result.Tokens);
        }

        return result.Error switch
        {
            AuthError.InvalidCredentials => ProblemResults.Create(StatusCodes.Status401Unauthorized, UserMessages.LoginIncorrect),
            AuthError.TemporarilyLocked => ProblemResults.Create(StatusCodes.Status423Locked, UserMessages.TooManyLoginAttempts),
            AuthError.AccessDenied => ProblemResults.Create(StatusCodes.Status403Forbidden, UserMessages.AccountUnusable),
            AuthError.TemporaryPasswordExpired => ProblemResults.Create(StatusCodes.Status403Forbidden, UserMessages.TemporaryPasswordExpired),
            _ => ProblemResults.Create(StatusCodes.Status401Unauthorized, UserMessages.SessionExpired),
        };
    }
}
