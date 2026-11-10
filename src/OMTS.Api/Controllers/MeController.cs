using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using OMTS.Api.Filters;
using OMTS.Application.Common.Constants;
using OMTS.Application.Common.Interfaces;
using OMTS.Application.Features.Authorization;
using OMTS.Application.Features.Authorization.Dtos;

namespace OMTS.Api.Controllers;

[ApiController]
[Route("api/me")]
[Authorize]
public sealed class MeController(ICurrentUser currentUser, PermissionService permissionService) : ControllerBase
{
    /// <summary>
    /// Identity of the caller, read from the access token, and the onboarding steps the account still owes
    /// (<c>phoneVerificationRequired</c>, <c>mustChangePassword</c>), taken from the same access state as the account gate.
    /// Reachable while either step is pending.
    /// </summary>
    [HttpGet]
    [AllowWhenPasswordChangeRequired]
    [AllowWhenPhoneUnverified]
    public async Task<IActionResult> Get(CancellationToken cancellationToken)
    {
        var access = await permissionService.GetAccessAsync(currentUser.AccountId!.Value, cancellationToken);
        if (access is null)
        {
            return Unauthorized();
        }

        return Ok(new
        {
            accountId = currentUser.AccountId,
            sessionId = currentUser.SessionId,
            username = User.Identity?.Name,
            subjectType = User.FindFirst(OmtsClaimTypes.SubjectType)?.Value,
            mustChangePassword = access.RequiresPasswordChange,
            phoneVerificationRequired = access.RequiresPhoneVerification,
        });
    }

    /// <summary>Effective permissions per function and zone, for building web/mobile menus.</summary>
    [HttpGet("permissions")]
    [ProducesResponseType<MyPermissions>(StatusCodes.Status200OK)]
    public async Task<IActionResult> GetPermissions(CancellationToken cancellationToken)
    {
        var permissions = await permissionService.GetMyPermissionsAsync(currentUser.AccountId!.Value, cancellationToken);
        return permissions is null ? Unauthorized() : Ok(permissions);
    }
}
