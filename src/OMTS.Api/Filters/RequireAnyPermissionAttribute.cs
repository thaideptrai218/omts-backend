using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using OMTS.Api.Common;
using OMTS.Application.Common.Interfaces;
using OMTS.Application.Common.Messages;
using OMTS.Application.Features.Authorization;
using OMTS.Application.Features.Authorization.Models;

namespace OMTS.Api.Filters;

/// <summary>
/// Requires one of two permissions on records with no zone: the endpoint is read by two different screens (the cash book and its
/// settings), and holding either right opens it. <see cref="RequirePermissionAttribute"/> on several attributes needs all of them.
/// </summary>
[AttributeUsage(AttributeTargets.Method)]
public sealed class RequireAnyPermissionAttribute(string function, PermissionAction action, string otherFunction, PermissionAction otherAction)
    : Attribute, IAsyncAuthorizationFilter
{
    public string Function { get; } = PermissionFunctions.Exists(function)
        ? function
        : throw new ArgumentException($"Unknown permission function '{function}'.", nameof(function));

    public string OtherFunction { get; } = PermissionFunctions.Exists(otherFunction)
        ? otherFunction
        : throw new ArgumentException($"Unknown permission function '{otherFunction}'.", nameof(otherFunction));

    public async Task OnAuthorizationAsync(AuthorizationFilterContext context)
    {
        var services = context.HttpContext.RequestServices;
        var accountId = services.GetRequiredService<ICurrentUser>().AccountId;
        if (context.HttpContext.User.Identity?.IsAuthenticated != true || accountId is null)
        {
            context.Result = new ChallengeResult();
            return;
        }

        var access = await services.GetRequiredService<PermissionService>()
            .GetAccessAsync(accountId.Value, context.HttpContext.RequestAborted);

        if (access is null
            || !(access.Allows(Function, action, PermissionScope.NoZone, null) || access.Allows(OtherFunction, otherAction, PermissionScope.NoZone, null)))
        {
            context.Result = ProblemResults.Create(StatusCodes.Status403Forbidden, UserMessages.NoPermission);
        }
    }
}
