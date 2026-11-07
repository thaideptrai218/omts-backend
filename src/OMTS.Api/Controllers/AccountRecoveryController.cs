using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using OMTS.Api.Common;
using OMTS.Api.Extensions;
using OMTS.Application.Common.Messages;
using OMTS.Application.Features.Accounts;
using OMTS.Application.Features.Accounts.Dtos;

namespace OMTS.Api.Controllers;

/// <summary>Trader registration (ACS-01) and password recovery (ACS-03); both before login.</summary>
[ApiController]
[Route("api/auth")]
[EnableRateLimiting(RateLimitingExtensions.CodeRequests)]
[AllowAnonymous]
public sealed class AccountRecoveryController(RegistrationService registration, PasswordRecoveryService recovery) : ControllerBase
{
    /// <summary>ACS-01 – creates a trader account from a verified phone number or email.</summary>
    [HttpPost("register")]
    [ProducesResponseType<TraderRegistered>(StatusCodes.Status201Created)]
    public async Task<IActionResult> Register(RegisterTraderRequest request, CancellationToken cancellationToken) =>
        (await registration.RegisterAsync(request, cancellationToken)).ToActionResult(r => StatusCode(StatusCodes.Status201Created, r));

    /// <summary>ACS-03 – always 202 with MSG-ACCOUNT-20; a code is sent only to a verified identifier of an Active account.</summary>
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
