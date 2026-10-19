using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using OMTS.Api.Authorization;
using OMTS.Application.Authentication;
using OMTS.Application.Authorization;
using OMTS.Application.Common.Interfaces;

namespace OMTS.Api.Controllers;

[ApiController]
[Route("api/me")]
[Authorize]
public sealed class MeController(ICurrentUser currentUser, PermissionService permissionService) : ControllerBase
{
    /// <summary>Identity of the caller, read from the access token.</summary>
    [HttpGet]
    [AllowWhenPasswordChangeRequired]
    [AllowWhenPhoneUnverified]
    public IActionResult Get() => Ok(new
    {
        accountId = currentUser.AccountId,
        sessionId = currentUser.SessionId,
        username = User.Identity?.Name,
        subjectType = User.FindFirst(OmtsClaimTypes.SubjectType)?.Value,
    });

    /// <summary>Effective permissions per function and zone, for building web/mobile menus.</summary>
    [HttpGet("permissions")]
    [ProducesResponseType<MyPermissions>(StatusCodes.Status200OK)]
    public async Task<IActionResult> GetPermissions(CancellationToken cancellationToken)
    {
        var permissions = await permissionService.GetMyPermissionsAsync(currentUser.AccountId!.Value, cancellationToken);
        return permissions is null ? Unauthorized() : Ok(permissions);
    }
}
