using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using OMTS.Api.Common;
using OMTS.Application.Common.Interfaces;
using OMTS.Application.Common.Messages;
using OMTS.Application.Features.Authorization;
using OMTS.Application.Features.Authorization.Models;

namespace OMTS.Api.Filters;

/// <summary>
/// Passes when either of two function grants allows the request (no route zone: AnyZone or AllZones scopes only).
/// Used where two roles read the same records, e.g. seasonal workers for LABOR_WORKERS viewers and the Accountant.
/// </summary>
[AttributeUsage(AttributeTargets.Method)]
public sealed class RequireEitherPermissionAttribute(
    string firstFunction, PermissionAction firstAction, PermissionScope firstScope,
    string secondFunction, PermissionAction secondAction, PermissionScope secondScope) : Attribute, IAsyncAuthorizationFilter
{
    public async Task OnAuthorizationAsync(AuthorizationFilterContext context)
    {
        if (firstScope == PermissionScope.ZoneScoped || secondScope == PermissionScope.ZoneScoped)
        {
            throw new InvalidOperationException("RequireEitherPermission supports AnyZone and AllZones scopes only.");
        }

        var services = context.HttpContext.RequestServices;
        var accountId = services.GetRequiredService<ICurrentUser>().AccountId;
        if (context.HttpContext.User.Identity?.IsAuthenticated != true || accountId is null)
        {
            context.Result = new ChallengeResult();
            return;
        }

        var access = await services.GetRequiredService<PermissionService>().GetAccessAsync(accountId.Value, context.HttpContext.RequestAborted);
        var allowed = access is not null
            && (access.Allows(firstFunction, firstAction, firstScope, null) || access.Allows(secondFunction, secondAction, secondScope, null));
        if (!allowed)
        {
            context.Result = ProblemResults.Create(StatusCodes.Status403Forbidden, UserMessages.NoPermission);
        }
    }
}
