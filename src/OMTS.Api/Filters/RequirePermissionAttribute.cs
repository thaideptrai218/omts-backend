using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using OMTS.Api.Common;
using OMTS.Application.Common.Interfaces;
using OMTS.Application.Common.Messages;
using OMTS.Application.Features.Authorization;
using OMTS.Application.Features.Authorization.Models;

namespace OMTS.Api.Filters;

/// <summary>
/// Requires the caller's effective zone–role grants to allow <paramref name="action"/> on
/// <paramref name="function"/>. With <see cref="PermissionScope.ZoneScoped"/> the zone is read from the
/// route or query value named <see cref="ZoneParameter"/>. Handlers working on a single record must still
/// check that record's zone (UC02 postconditions: each function re-checks the record).
/// </summary>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Method, AllowMultiple = true)]
public sealed class RequirePermissionAttribute(string function, PermissionAction action, PermissionScope scope = PermissionScope.ZoneScoped)
    : Attribute, IAsyncAuthorizationFilter
{
    public string Function { get; } = PermissionFunctions.Exists(function)
        ? function
        : throw new ArgumentException($"Unknown permission function '{function}'.", nameof(function));

    public PermissionAction Action { get; } = action;
    public PermissionScope Scope { get; } = scope;
    public string ZoneParameter { get; init; } = "zoneId";

    public async Task OnAuthorizationAsync(AuthorizationFilterContext context)
    {
        var services = context.HttpContext.RequestServices;
        var accountId = services.GetRequiredService<ICurrentUser>().AccountId;
        if (context.HttpContext.User.Identity?.IsAuthenticated != true || accountId is null)
        {
            context.Result = new ChallengeResult();
            return;
        }

        Guid? zoneId = null;
        if (Scope == PermissionScope.ZoneScoped)
        {
            var raw = context.RouteData.Values[ZoneParameter]?.ToString() ?? context.HttpContext.Request.Query[ZoneParameter].ToString();
            if (!Guid.TryParse(raw, out var parsed))
            {
                context.Result = ProblemResults.InvalidField(ZoneParameter);
                return;
            }

            zoneId = parsed;
        }

        var access = await services.GetRequiredService<PermissionService>()
            .GetAccessAsync(accountId.Value, context.HttpContext.RequestAborted);

        if (access is null || !access.Allows(Function, Action, Scope, zoneId))
        {
            context.Result = ProblemResults.Create(StatusCodes.Status403Forbidden, UserMessages.NoPermission);
        }
    }
}
