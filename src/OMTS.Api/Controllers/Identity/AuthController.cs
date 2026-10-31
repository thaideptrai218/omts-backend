using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using OMTS.Api.Common;
using OMTS.Api.Extensions;
using OMTS.Api.Filters;
using OMTS.Application.Common.Interfaces;
using OMTS.Application.Common.Messages;
using OMTS.Application.Features.Auth.Dtos;
using OMTS.Application.Features.Auth;

namespace OMTS.Api.Controllers.Identity;

[ApiController]
[Route("api/auth")]
public sealed class AuthController(AuthService authService, ZaloLinkService zaloLinks, ICurrentUser currentUser) : ControllerBase
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

    /// <summary>
    /// Zalo Mini App "Liên kết tài khoản": the Zalo phone number finds the account the cooperative created, links
    /// the Zalo user to it and opens a session. 404 ZALO_PHONE_NOT_FOUND (with <c>maskedPhone</c>) creates nothing;
    /// a trader then sends <c>zalo/trader-requests</c>. 403 ZALO_APPROVAL_PENDING / ZALO_REQUEST_REJECTED (<c>reason</c>).
    /// </summary>
    [EnableRateLimiting(RateLimitingExtensions.Logins)]
    [HttpPost("zalo/link")]
    [AllowAnonymous]
    [ProducesResponseType<ZaloLinked>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status403Forbidden)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status503ServiceUnavailable)]
    public async Task<IActionResult> LinkZalo(ZaloLinkRequest request, CancellationToken cancellationToken) =>
        (await zaloLinks.LinkAsync(request, HttpContext.ClientInfo(), cancellationToken)).ToActionResult(Ok);

    /// <summary>
    /// A trader whose Zalo number has no account asks the cooperative for one (name, optional note). The account
    /// waits in Pending approval until the Cooperative Owner approves it; the Owners get a notification.
    /// </summary>
    [EnableRateLimiting(RateLimitingExtensions.Logins)]
    [HttpPost("zalo/trader-requests")]
    [AllowAnonymous]
    [ProducesResponseType<TraderRequestSent>(StatusCodes.Status202Accepted)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status503ServiceUnavailable)]
    public async Task<IActionResult> RequestTraderAccess(ZaloTraderRequest request, CancellationToken cancellationToken) =>
        (await zaloLinks.RequestTraderAccessAsync(request, cancellationToken)).ToActionResult(sent => StatusCode(StatusCodes.Status202Accepted, sent));

    /// <summary>A session for the account already linked to this Zalo user; 404 ZALO_NOT_LINKED otherwise.</summary>
    [EnableRateLimiting(RateLimitingExtensions.Logins)]
    [HttpPost("zalo/login")]
    [AllowAnonymous]
    [ProducesResponseType<AuthTokens>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status403Forbidden)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status503ServiceUnavailable)]
    public async Task<IActionResult> LoginWithZalo(ZaloLoginRequest request, CancellationToken cancellationToken) =>
        (await zaloLinks.LoginAsync(request, HttpContext.ClientInfo(), cancellationToken)).ToActionResult(Ok);

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
