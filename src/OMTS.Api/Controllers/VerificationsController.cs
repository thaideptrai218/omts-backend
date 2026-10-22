using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using OMTS.Api.Common;
using OMTS.Api.Filters;
using OMTS.Application.Common.Interfaces;
using OMTS.Application.Features.Accounts;
using OMTS.Application.Features.Verification;
using OMTS.Application.Features.Verification.Dtos;

namespace OMTS.Api.Controllers;

/// <summary>
/// ACS-06 – verification codes. Starting a request here is for registration only; logged-in flows start
/// theirs from their own endpoints (phone verification, contact change, password change, recovery).
/// Resend and verify work for any request the caller may use.
/// </summary>
[ApiController]
[Route("api/verifications")]
[AllowAnonymous]
public sealed class VerificationsController(
    RegistrationService registration, VerificationService verification, ICurrentUser currentUser) : ControllerBase
{
    /// <summary>Registration (ACS-01): checks the identifier is free, then sends the code.</summary>
    [HttpPost]
    [ProducesResponseType<VerificationStarted>(StatusCodes.Status201Created)]
    public async Task<IActionResult> Start(StartVerificationRequest request, CancellationToken cancellationToken) =>
        (await registration.StartVerificationAsync(request, cancellationToken)).ToActionResult(v => StatusCode(StatusCodes.Status201Created, v));

    [HttpPost("{id:guid}/resend")]
    [AllowWhenPasswordChangeRequired]
    [AllowWhenPhoneUnverified]
    [ProducesResponseType<VerificationStarted>(StatusCodes.Status200OK)]
    public async Task<IActionResult> Resend(Guid id, CancellationToken cancellationToken) =>
        (await verification.ResendAsync(id, currentUser.AccountId, cancellationToken)).ToActionResult(Ok);

    /// <summary>A correct code returns a single-use verification token (10 minutes) for the next step.</summary>
    [HttpPost("{id:guid}/verify")]
    [AllowWhenPasswordChangeRequired]
    [AllowWhenPhoneUnverified]
    [ProducesResponseType<VerificationTokenIssued>(StatusCodes.Status200OK)]
    public async Task<IActionResult> Verify(Guid id, VerifyCodeRequest request, CancellationToken cancellationToken) =>
        (await verification.VerifyAsync(id, request.Code, currentUser.AccountId, cancellationToken)).ToActionResult(Ok);
}
