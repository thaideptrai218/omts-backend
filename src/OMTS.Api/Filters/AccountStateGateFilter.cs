using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc.Filters;
using OMTS.Api.Common;
using OMTS.Application.Common.Interfaces;
using OMTS.Application.Common.Messages;
using OMTS.Application.Features.Authorization;

namespace OMTS.Api.Filters;

/// <summary>
/// Global gate for authenticated requests, in the order of ACCOUNT-02: first a verified phone number (step 5,
/// MSG-ACCOUNT-04, for every account), then the change of a temporary password (step 6). Only endpoints marked with the
/// attribute of the current step stay reachable meanwhile. Runs before <see cref="RequirePermissionAttribute"/>.
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

        if (access.RequiresPhoneVerification)
        {
            if (!metadata.OfType<AllowWhenPhoneUnverifiedAttribute>().Any())
            {
                context.Result = ProblemResults.Create(StatusCodes.Status403Forbidden, UserMessages.PhoneVerificationRequired);
            }
        }
        else if (access.RequiresPasswordChange && !metadata.OfType<AllowWhenPasswordChangeRequiredAttribute>().Any())
        {
            context.Result = ProblemResults.Create(StatusCodes.Status403Forbidden, UserMessages.PasswordChangeRequired);
        }
    }
}
