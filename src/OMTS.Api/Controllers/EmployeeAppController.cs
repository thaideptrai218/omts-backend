using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using OMTS.Api.Common;
using OMTS.Api.Extensions;
using OMTS.Application.Common.Interfaces;
using OMTS.Application.Features.EmployeeApp;

namespace OMTS.Api.Controllers;

/// <summary>
/// The employee app (LABOR-06, BR-LABOR-02): a second kind of caller, never a staff account. Login and refresh are anonymous; the rest
/// takes the token of the employee app scheme only, so a staff token is refused here (401) as an employee token is refused everywhere else.
/// </summary>
[ApiController]
[Route("api/employee-app")]
[Authorize(AuthenticationSchemes = EmployeeAppAuthentication.Scheme)]
public sealed class EmployeeAppController(
    EmployeeAppLoginService login, EmployeeAppSessionService sessions, EmployeeDeviceService devices, ICurrentEmployee currentEmployee) : ControllerBase
{
    /// <summary>
    /// LABOR-06 – log in with the phone number of the profile. The linked device sends <c>deviceId</c> and <c>deviceSecret</c> and needs no
    /// code; any other device sends the <c>loginCode</c> a Zone Manager or the Owner issued, and becomes the linked device (the device linked
    /// before is logged out). Without a code 401 MSG-LABOR-17 with <c>loginCodeRequired</c>; an unknown phone number 401 MSG-LABOR-18; a
    /// wrong, expired, spent or cancelled code 401 MSG-LABOR-19. The answer that linked a device carries its <c>deviceSecret</c> once.
    /// </summary>
    [EnableRateLimiting(RateLimitingExtensions.EmployeeAppLogins)]
    [ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)] // carries tokens and the device secret: never stored (Cache-Control no-store, Pragma no-cache)
    [HttpPost("login")]
    [AllowAnonymous]
    [ProducesResponseType<EmployeeAppTokens>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status401Unauthorized)]
    public async Task<IActionResult> Login(EmployeeAppLoginRequest request, CancellationToken cancellationToken) =>
        (await login.LoginAsync(request, cancellationToken)).ToActionResult(Ok);

    /// <summary>
    /// Exchanges a refresh token for new tokens, as <c>POST /api/auth/refresh</c> does for a staff session (rotation, a 30 second grace for
    /// the replaced token, replay ends the session). A refusal is 401 with the problem extension <c>reason</c>: <c>expired</c>, <c>reuse</c> or <c>revoked</c>.
    /// </summary>
    [ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
    [HttpPost("refresh")]
    [AllowAnonymous]
    [ProducesResponseType<EmployeeAppTokens>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status401Unauthorized)]
    public async Task<IActionResult> Refresh(EmployeeAppRefreshRequest request, CancellationToken cancellationToken) =>
        (await sessions.RefreshAsync(request, cancellationToken)).ToActionResult(Ok);

    /// <summary>Ends this session; the device stays linked, so the next login on it needs no code.</summary>
    [HttpPost("logout")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    public async Task<IActionResult> Logout(CancellationToken cancellationToken)
    {
        if (currentEmployee is not { EmployeeId: { } employeeId, SessionId: { } sessionId })
        {
            return Unauthorized();
        }

        await sessions.LogoutAsync(employeeId, sessionId, cancellationToken);
        return NoContent();
    }

    /// <summary>The push token of this phone (BR-LABOR-04); an empty <c>pushToken</c> clears it.</summary>
    [HttpPut("device/push-token")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> SetPushToken(EmployeePushTokenRequest request, CancellationToken cancellationToken)
    {
        if (currentEmployee is not { EmployeeId: { } employeeId, DeviceId: { } deviceId })
        {
            return Unauthorized();
        }

        return (await devices.SetPushTokenAsync(employeeId, deviceId, request, cancellationToken)).ToActionResult(_ => NoContent());
    }
}
