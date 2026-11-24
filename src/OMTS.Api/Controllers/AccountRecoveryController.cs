using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using OMTS.Api.Common;
using OMTS.Api.Extensions;
using OMTS.Application.Common.Messages;
using OMTS.Application.Features.Accounts;
using OMTS.Application.Features.Accounts.Dtos;

namespace OMTS.Api.Controllers;

/// <summary>Password recovery (ACS-03), before login. A trader does not register here: the Owner makes the account from a trader account request (ACCOUNT-11).</summary>
[ApiController]
[Route("api/auth")]
[EnableRateLimiting(RateLimitingExtensions.CodeRequests)]
[AllowAnonymous]
public sealed class AccountRecoveryController(PasswordRecoveryService recovery) : ControllerBase
{
    /// <summary>ACS-03 – always 202 with MSG-ACCOUNT-20; an SMS code is sent only to the verified phone number of an Active account. An email identifier gets the same answer and no code.</summary>
    [HttpPost("recovery")]
    [ProducesResponseType<RecoveryStarted>(StatusCodes.Status202Accepted)]
    public async Task<IActionResult> StartRecovery(StartRecoveryRequest request, CancellationToken cancellationToken) =>
        Accepted(await recovery.StartAsync(request, cancellationToken));

    /// <summary>
    /// Sets the new password with the token from <c>POST api/verifications/{id}/verify</c>; every session ends.
    /// Answers MSG-ACCOUNT-10 (<c>message</c>, <c>messageCode</c>).
    /// </summary>
    [HttpPost("recovery/reset")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public async Task<IActionResult> Reset(ResetPasswordRequest request, CancellationToken cancellationToken) =>
        (await recovery.ResetAsync(request, cancellationToken)).ToActionResult(
            _ => Ok(new { message = UserMessages.PasswordRecovered.Text, messageCode = UserMessages.PasswordRecovered.Code }));
}
