using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using OMTS.Api.Common;
using OMTS.Api.Extensions;
using OMTS.Api.Filters;
using OMTS.Application.Common.Interfaces;
using OMTS.Application.Features.Accounts.Dtos;
using OMTS.Application.Features.Accounts;
using OMTS.Application.Features.Auth;
using OMTS.Application.Features.Verification.Dtos;

namespace OMTS.Api.Controllers.Identity;

/// <summary>
/// The caller's password (ACS-04), the phone verification after login (ACS-02) and the Zalo link of the Mini
/// App. Reachable while a temporary password must be changed and while the phone is unverified.
/// </summary>
[ApiController]
[Route("api/me")]
[Authorize]
[AllowWhenPasswordChangeRequired]
[AllowWhenPhoneUnverified]
public sealed class MeSecurityController(
    PasswordChangeService passwords, PhoneVerificationService phones, ZaloLinkService zaloLinks, ICurrentUser currentUser) : ControllerBase
{
    /// <summary>Proof by current password or by an email code; all sessions end and fresh tokens are returned (MSG18).</summary>
    [HttpPost("password")]
    [ProducesResponseType<PasswordChanged>(StatusCodes.Status200OK)]
    public async Task<IActionResult> ChangePassword(ChangePasswordRequest request, CancellationToken cancellationToken) =>
        (await passwords.ChangeAsync(currentUser.AccountId!.Value, currentUser.SessionId, request, HttpContext.ClientInfo(), cancellationToken)).ToActionResult(Ok);

    /// <summary>Sends a ChangePassword code to the verified email (NO_VERIFIED_EMAIL guidance without one).</summary>
    [HttpPost("password/code")]
    [EnableRateLimiting(RateLimitingExtensions.CodeRequests)]
    [ProducesResponseType<VerificationStarted>(StatusCodes.Status201Created)]
    public async Task<IActionResult> SendPasswordCode(CancellationToken cancellationToken) =>
        (await passwords.SendEmailCodeAsync(currentUser.AccountId!.Value, cancellationToken)).ToActionResult(v => StatusCode(StatusCodes.Status201Created, v));

    [HttpPost("phone-verification")]
    [EnableRateLimiting(RateLimitingExtensions.CodeRequests)]
    [ProducesResponseType<VerificationStarted>(StatusCodes.Status201Created)]
    public async Task<IActionResult> StartPhoneVerification(StartPhoneVerificationRequest request, CancellationToken cancellationToken) =>
        (await phones.StartAsync(currentUser.AccountId!.Value, request, cancellationToken)).ToActionResult(v => StatusCode(StatusCodes.Status201Created, v));

    [HttpPost("phone-verification/confirm")]
    [ProducesResponseType<ContactConfirmed>(StatusCodes.Status200OK)]
    public async Task<IActionResult> ConfirmPhoneVerification(ConfirmContactRequest request, CancellationToken cancellationToken) =>
        (await phones.ConfirmAsync(currentUser.AccountId!.Value, request, cancellationToken)).ToActionResult(Ok);

    /// <summary>Removes the Zalo link (the account stays) and ends the current session; repeating it is harmless.</summary>
    [HttpDelete("zalo-link")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    public async Task<IActionResult> UnlinkZalo(CancellationToken cancellationToken) =>
        (await zaloLinks.UnlinkAsync(currentUser.AccountId!.Value, currentUser.SessionId!.Value, cancellationToken)).ToActionResult(_ => NoContent());
}
