using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc.Filters;
using OMTS.Api.Common;
using OMTS.Application.Common.Interfaces;
using OMTS.Application.Common.Messages;
using OMTS.Application.Features.Authorization;

namespace OMTS.Api.Filters;

/// <summary>
/// Global gate for authenticated requests: a temporary password must be changed first (UC02 branch 2c),
/// then traders and staff must verify a phone number (BR-34, MSG67). Only endpoints marked with the
/// matching attribute stay reachable meanwhile. Runs before <see cref="RequirePermissionAttribute"/>.
/// </summary>
public sealed class AccountStateGateFilter(ICurrentUser currentUser, PermissionService permissionService) : IAsyncAuthorizationFilter
{
    public async Task OnAuthorizationAsync(AuthorizationFilterContext context)
    {
        var metadata = context.ActionDescriptor.EndpointMetadata;
        if (metadata.OfType<IAllowAnonymous>().Any()
            || context.HttpContext.User.Identity?.IsAuthenticated != true
            || currentUser.AccountId is not { } accountId)
        {
            return;
        }

        var access = await permissionService.GetAccessAsync(accountId, context.HttpContext.RequestAborted);
        if (access is null)
        {
            return;
        }

        if (access.RequiresPasswordChange && !metadata.OfType<AllowWhenPasswordChangeRequiredAttribute>().Any())
        {
            context.Result = ProblemResults.Create(StatusCodes.Status403Forbidden, UserMessages.PasswordChangeRequired);
        }
        else if (access.RequiresPhoneVerification && !metadata.OfType<AllowWhenPhoneUnverifiedAttribute>().Any())
        {
            context.Result = ProblemResults.Create(StatusCodes.Status403Forbidden, UserMessages.PhoneVerificationRequired);
        }
    }
}
