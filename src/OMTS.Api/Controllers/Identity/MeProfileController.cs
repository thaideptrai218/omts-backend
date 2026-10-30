using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using OMTS.Api.Common;
using OMTS.Api.Extensions;
using OMTS.Api.Filters;
using OMTS.Application.Common.Interfaces;
using OMTS.Application.Features.Accounts.Dtos;
using OMTS.Application.Features.Accounts;

namespace OMTS.Api.Controllers.Identity;

/// <summary>ACS-05 personal profile and ACS-07 notification settings of the caller.</summary>
[ApiController]
[Route("api/me")]
[Authorize]
public sealed class MeProfileController(
    ProfileService profiles,
    NotificationPreferenceService preferences,
    PushDeviceService devices,
    ICurrentUser currentUser) : ControllerBase
{
    private Guid AccountId => currentUser.AccountId!.Value;

    /// <summary>Open before the phone is verified, so staff can see the number the Owner entered.</summary>
    [HttpGet("profile")]
    [AllowWhenPhoneUnverified]
    [ProducesResponseType<ProfileView>(StatusCodes.Status200OK)]
    public async Task<IActionResult> GetProfile(CancellationToken cancellationToken) =>
        (await profiles.GetAsync(AccountId, cancellationToken)).ToActionResult(Ok);

    /// <summary>Display name only; a username in the body is refused (MSG20). Requires the read rowVersion.</summary>
    [HttpPut("profile")]
    [ProducesResponseType<ProfileView>(StatusCodes.Status200OK)]
    public async Task<IActionResult> UpdateProfile(UpdateProfileRequest request, CancellationToken cancellationToken) =>
        (await profiles.UpdateAsync(AccountId, request, cancellationToken)).ToActionResult(r => Ok(new { profile = r.Profile, message = r.Message }));

    /// <summary>Sends a code to a new email or phone number (MSG62); the old one stays until verified.</summary>
    [HttpPost("contact-changes")]
    [EnableRateLimiting(RateLimitingExtensions.CodeRequests)]
    [ProducesResponseType<ContactChangeStarted>(StatusCodes.Status201Created)]
    public async Task<IActionResult> StartContactChange(StartContactChangeRequest request, CancellationToken cancellationToken) =>
        (await profiles.StartContactChangeAsync(AccountId, request, cancellationToken)).ToActionResult(r => StatusCode(StatusCodes.Status201Created, r));

    [HttpPost("contact-changes/confirm")]
    [ProducesResponseType<ContactConfirmed>(StatusCodes.Status200OK)]
    public async Task<IActionResult> ConfirmContactChange(ConfirmContactRequest request, CancellationToken cancellationToken) =>
        (await profiles.ConfirmContactChangeAsync(AccountId, request, cancellationToken)).ToActionResult(Ok);

    [HttpGet("notification-preferences")]
    [ProducesResponseType<IReadOnlyList<CategoryPreferenceView>>(StatusCodes.Status200OK)]
    public async Task<IActionResult> GetNotificationPreferences(CancellationToken cancellationToken) =>
        (await preferences.GetAsync(AccountId, cancellationToken)).ToActionResult(Ok);

    [HttpPut("notification-preferences")]
    [ProducesResponseType<NotificationPreferencesSaved>(StatusCodes.Status200OK)]
    public async Task<IActionResult> SaveNotificationPreferences(SaveNotificationPreferencesRequest request, CancellationToken cancellationToken) =>
        (await preferences.SaveAsync(AccountId, request, cancellationToken)).ToActionResult(Ok);

    /// <summary>
    /// Registers the Expo push token of this device for the current login session: call it after every
    /// login and at every app start. Logging out of the session stops the device's notifications; a password
    /// change on this device keeps them.
    /// </summary>
    [HttpPost("devices")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    public async Task<IActionResult> RegisterDevice(RegisterPushDeviceRequest request, CancellationToken cancellationToken) =>
        (await devices.RegisterAsync(AccountId, currentUser.SessionId!.Value, request, cancellationToken)).ToActionResult(_ => NoContent());

    [HttpDelete("devices/{token}")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    public async Task<IActionResult> UnregisterDevice(string token, CancellationToken cancellationToken)
    {
        await devices.UnregisterAsync(AccountId, token, cancellationToken);
        return NoContent();
    }
}
