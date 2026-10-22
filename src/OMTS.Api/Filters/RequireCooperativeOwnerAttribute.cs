using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using OMTS.Api.Common;
using OMTS.Application.Common.Interfaces;
using OMTS.Application.Common.Messages;
using OMTS.Application.Features.Authorization;

namespace OMTS.Api.Filters;

/// <summary>
/// Owner-only use cases (accounts, grants, roles, cooperative profile, employees, zones…): checks the
/// account's subject type, never role permissions, so a custom role can never open them. Others get
/// 403 MSG02; no token gives 401.
/// </summary>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Method)]
public sealed class RequireCooperativeOwnerAttribute : Attribute, IAsyncAuthorizationFilter
{
    public async Task OnAuthorizationAsync(AuthorizationFilterContext context)
    {
        var services = context.HttpContext.RequestServices;
        var accountId = services.GetRequiredService<ICurrentUser>().AccountId;
        if (context.HttpContext.User.Identity?.IsAuthenticated != true || accountId is null)
        {
            context.Result = new ChallengeResult();
            return;
        }

        var access = await services.GetRequiredService<PermissionService>().GetAccessAsync(accountId.Value, context.HttpContext.RequestAborted);
        if (access?.IsCooperativeOwner != true)
        {
            context.Result = ProblemResults.Create(StatusCodes.Status403Forbidden, UserMessages.NoPermission);
        }
    }
}
