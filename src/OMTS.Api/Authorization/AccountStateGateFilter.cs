using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc.Filters;
using OMTS.Application.Authorization;
using OMTS.Application.Common.Interfaces;

namespace OMTS.Api.Authorization;

/// <summary>Endpoint stays open while the account must change its temporary password (UC04 endpoints).</summary>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Method)]
public sealed class AllowWhenPasswordChangeRequiredAttribute : Attribute;

/// <summary>Endpoint stays open while a trader/staff account has no verified phone (verification endpoints).</summary>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Method)]
public sealed class AllowWhenPhoneUnverifiedAttribute : Attribute;

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
            context.Result = Problems.Create(
                StatusCodes.Status403Forbidden, "PASSWORD_CHANGE_REQUIRED", "Change your temporary password before using other functions.");
        }
        else if (access.RequiresPhoneVerification && !metadata.OfType<AllowWhenPhoneUnverifiedAttribute>().Any())
        {
            context.Result = Problems.Create(
                StatusCodes.Status403Forbidden, "MSG67", "A verified phone number is required before using other functions.");
        }
    }
}
