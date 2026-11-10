using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using OMTS.Api.Common;
using OMTS.Application.Common.Interfaces;
using OMTS.Application.Features.Auth;
using OMTS.Application.Features.Auth.Dtos;

namespace OMTS.Api.Controllers;

/// <summary>
/// The caller's own login sessions and the devices (browsers, app installs) they belong to: "Thiết bị đang đăng nhập". Every
/// call is scoped to the caller's account; another account's session answers 404.
/// </summary>
[ApiController]
[Route("api/me")]
[Authorize]
public sealed class MeSessionsController(SessionManagementService sessionManagement, ICurrentUser currentUser) : ControllerBase
{
    /// <summary>Open sessions of the caller, newest first, with the address masked (<c>203.0.*.*</c>). <c>isCurrent</c> marks this request's session.</summary>
    [HttpGet("sessions")]
    [ProducesResponseType<IReadOnlyList<SessionView>>(StatusCodes.Status200OK)]
    public async Task<IActionResult> ListSessions(CancellationToken cancellationToken) =>
        Ok(await sessionManagement.ListSessionsAsync(currentUser.AccountId!.Value, currentUser.SessionId, cancellationToken));

    /// <summary>Ends one session, this one included (the next request is 401). 204 also when it had already ended; 404 when it is not the caller's.</summary>
    [HttpDelete("sessions/{id:guid}")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> RevokeSession(Guid id, CancellationToken cancellationToken) =>
        (await sessionManagement.RevokeSessionAsync(currentUser.AccountId!.Value, id, cancellationToken)).ToActionResult(_ => NoContent());

    /// <summary>Ends every open session except the current one.</summary>
    [HttpPost("sessions/revoke-others")]
    [ProducesResponseType<SessionsRevoked>(StatusCodes.Status200OK)]
    public async Task<IActionResult> RevokeOtherSessions(CancellationToken cancellationToken) =>
        Ok(await sessionManagement.RevokeOthersAsync(currentUser.AccountId!.Value, currentUser.SessionId, cancellationToken));

    /// <summary>
    /// Open sessions grouped by device, most recently active first. A session without a device is its own entry with a null
    /// <c>deviceKey</c>; end it with <c>DELETE sessions/{id}</c>.
    /// </summary>
    [HttpGet("devices")]
    [ProducesResponseType<IReadOnlyList<DeviceView>>(StatusCodes.Status200OK)]
    public async Task<IActionResult> ListDevices(CancellationToken cancellationToken) =>
        Ok(await sessionManagement.ListDevicesAsync(currentUser.AccountId!.Value, currentUser.SessionId, cancellationToken));

    /// <summary>
    /// Ends the caller's sessions on one device. Always 204, also when the device is unknown or already signed out. The route
    /// shares its prefix with <c>DELETE devices/{token}</c> (a push token), so a device key must have the exact shape of one:
    /// 43 base64url characters, the unpadded encoding of a SHA-256 value. A push token never has that shape.
    /// </summary>
    [HttpDelete("devices/{deviceKey:regex(^[[A-Za-z0-9_-]]{{43}}$)}")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    public async Task<IActionResult> RevokeDevice(string deviceKey, CancellationToken cancellationToken) =>
        (await sessionManagement.RevokeDeviceAsync(currentUser.AccountId!.Value, deviceKey, cancellationToken)).ToActionResult(_ => NoContent());
}
