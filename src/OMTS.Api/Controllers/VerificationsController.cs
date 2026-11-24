using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using OMTS.Api.Common;
using OMTS.Api.Extensions;
using OMTS.Api.Filters;
using OMTS.Application.Common.Interfaces;
using OMTS.Application.Features.Accounts;
using OMTS.Application.Features.Verification;
using OMTS.Application.Features.Verification.Dtos;

namespace OMTS.Api.Controllers;

/// <summary>
/// ACS-06 – verification codes. A request is started by the endpoint of its own flow (phone verification, contact change,
/// password change, recovery); resend and verify work here for any request the caller may use.
/// </summary>
[ApiController]
[Route("api/verifications")]
[EnableRateLimiting(RateLimitingExtensions.CodeRequests)]
[AllowAnonymous]
public sealed class VerificationsController(VerificationService verification, ICurrentUser currentUser) : ControllerBase
{
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
