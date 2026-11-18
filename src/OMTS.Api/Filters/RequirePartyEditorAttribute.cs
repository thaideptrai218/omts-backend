using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using OMTS.Api.Common;
using OMTS.Application.Common.Interfaces;
using OMTS.Application.Common.Messages;
using OMTS.Application.Features.Authorization;
using OMTS.Application.Features.Authorization.Models;

namespace OMTS.Api.Filters;

/// <summary>
/// Whoever may create or edit customers or suppliers (neither has a zone, so a grant in any zone is enough): the tax code lookup of their invoice forms.
/// Others get 403 MSG-GEN-08, before the request is read; no token gives 401.
/// </summary>
[AttributeUsage(AttributeTargets.Method)]
public sealed class RequirePartyEditorAttribute : Attribute, IAsyncAuthorizationFilter
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
        var may = access is not null && new[] { PermissionFunctions.Customers, PermissionFunctions.Suppliers }
            .Any(function => access.Allows(function, PermissionAction.Create, PermissionScope.NoZone, null) || access.Allows(function, PermissionAction.Edit, PermissionScope.NoZone, null));
        if (!may)
        {
            context.Result = ProblemResults.Create(StatusCodes.Status403Forbidden, UserMessages.NoPermission);
        }
    }
}
