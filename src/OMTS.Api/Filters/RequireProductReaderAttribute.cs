using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using OMTS.Api.Common;
using OMTS.Application.Common.Messages;
using OMTS.Application.Features.Sales.Products;

namespace OMTS.Api.Filters;

/// <summary>
/// The product reads: as <see cref="RequirePermissionAttribute"/> on other routes, a role with no right gets 403 MSG-GEN-08
/// before the request is read, so whatever it sends (GFR-02). Who may read is decided by <see cref="ProductReadService"/>.
/// </summary>
[AttributeUsage(AttributeTargets.Method)]
public sealed class RequireProductReaderAttribute : Attribute, IAsyncAuthorizationFilter
{
    public async Task OnAuthorizationAsync(AuthorizationFilterContext context)
    {
        if (context.HttpContext.User.Identity?.IsAuthenticated != true)
        {
            context.Result = new ChallengeResult();
            return;
        }

        var reads = context.HttpContext.RequestServices.GetRequiredService<ProductReadService>();
        if (!await reads.MayReadAsync(context.HttpContext.RequestAborted))
        {
            context.Result = ProblemResults.Create(StatusCodes.Status403Forbidden, UserMessages.NoPermission);
        }
    }
}
