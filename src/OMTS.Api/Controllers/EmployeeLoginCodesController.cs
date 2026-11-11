using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using OMTS.Api.Common;
using OMTS.Api.Extensions;
using OMTS.Api.Filters;
using OMTS.Application.Features.Authorization;
using OMTS.Application.Features.Authorization.Models;
using OMTS.Application.Features.EmployeeApp;

namespace OMTS.Api.Controllers;

/// <summary>
/// LABOR-03 – the login code of the employee app (BR-LABOR-02): the Cooperative Owner, or a Zone Manager of the employee (a shared working
/// zone). A staff token like any other: nothing here accepts an employee app token.
/// </summary>
[ApiController]
[Route("api/employees/{id:guid}/login-codes")]
[Authorize]
public sealed class EmployeeLoginCodesController(EmployeeLoginCodeService loginCodes) : ControllerBase
{
    /// <summary>
    /// Issues a code for the employee and cancels the earlier one; the answer is the only place the code is shown besides the notification
    /// to the caller's own account. 409 MSG-LABOR-09 (<c>reason</c> <c>Left</c> or <c>NoPhone</c>) when the employee has left or has no phone
    /// number; 403 for a Zone Manager who does not maintain the employee.
    /// </summary>
    [EnableRateLimiting(RateLimitingExtensions.CodeRequests)]
    [HttpPost]
    [RequirePermission(PermissionFunctions.LaborWorkers, PermissionAction.Create, PermissionScope.AnyZone)]
    [ProducesResponseType<EmployeeLoginCodeIssued>(StatusCodes.Status201Created)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status403Forbidden)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict)]
    public async Task<IActionResult> Issue(Guid id, CancellationToken cancellationToken) =>
        (await loginCodes.IssueAsync(id, cancellationToken)).ToActionResult(issued => StatusCode(StatusCodes.Status201Created, issued));
}
