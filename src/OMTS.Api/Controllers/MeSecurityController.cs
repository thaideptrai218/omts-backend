using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using OMTS.Api.Common;
using OMTS.Api.Extensions;
using OMTS.Api.Filters;
using OMTS.Application.Common.Interfaces;
using OMTS.Application.Features.Accounts;
using OMTS.Application.Features.Accounts.Dtos;
using OMTS.Application.Features.Verification.Dtos;

namespace OMTS.Api.Controllers;

/// <summary>
/// The caller's password (ACCOUNT-04) and the phone verification after login (ACCOUNT-02.1). Reachable while a temporary
/// password must be changed; while the phone is unverified, only the phone verification is (MSG-ACCOUNT-04).
/// </summary>
[ApiController]
[Route("api/me")]
[Authorize]
[AllowWhenPasswordChangeRequired]
public sealed class MeSecurityController(
    PasswordChangeService passwords, PhoneVerificationService phones, ICurrentUser currentUser) : ControllerBase
{
    /// <summary>
    /// Proof by current password or by an email code, none right after a login with a temporary password (ACCOUNT-04.1);
    /// all sessions end and fresh tokens are returned (MSG-ACCOUNT-11).
    /// </summary>
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
    [AllowWhenPhoneUnverified]
    [EnableRateLimiting(RateLimitingExtensions.CodeRequests)]
    [ProducesResponseType<VerificationStarted>(StatusCodes.Status201Created)]
    public async Task<IActionResult> StartPhoneVerification(StartPhoneVerificationRequest request, CancellationToken cancellationToken) =>
        (await phones.StartAsync(currentUser.AccountId!.Value, request, cancellationToken)).ToActionResult(v => StatusCode(StatusCodes.Status201Created, v));

    [HttpPost("phone-verification/confirm")]
    [AllowWhenPhoneUnverified]
    [ProducesResponseType<ContactConfirmed>(StatusCodes.Status200OK)]
    public async Task<IActionResult> ConfirmPhoneVerification(ConfirmContactRequest request, CancellationToken cancellationToken) =>
        (await phones.ConfirmAsync(currentUser.AccountId!.Value, request, cancellationToken)).ToActionResult(Ok);
}
