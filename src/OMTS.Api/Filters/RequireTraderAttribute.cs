using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using OMTS.Api.Common;
using OMTS.Application.Common.Interfaces;
using OMTS.Application.Common.Messages;
using OMTS.Application.Features.Authorization;
using OMTS.Domain.Enums;

namespace OMTS.Api.Filters;

/// <summary>
/// Trader-only use cases (§I.4.4 Full†): checks the subject type, because traders never go through the role permission matrix.
/// Others get 403 MSG-GEN-08, the Owner too; no token gives 401. Each service then keeps to the trader's own data.
/// </summary>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Method)]
public sealed class RequireTraderAttribute : Attribute, IAsyncAuthorizationFilter
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
        if (access?.SubjectType != SubjectType.Trader)
        {
            context.Result = ProblemResults.Create(StatusCodes.Status403Forbidden, UserMessages.NoPermission);
        }
    }
}
