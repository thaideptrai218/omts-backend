using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using OMTS.Api.Common;
using OMTS.Api.Filters;
using OMTS.Application.Features.Authorization;
using OMTS.Application.Features.Authorization.Models;
using OMTS.Application.Features.Labor;

namespace OMTS.Api.Controllers;

/// <summary>
/// The salary setup of an employee (tab "Thiết lập lương"): the same people as the hourly rates of the job types, the Accountant and
/// the Cooperative Owner (PAY_RULES).
/// </summary>
[ApiController]
[Route("api/employees/{id:guid}/salary")]
[Authorize]
public sealed class EmployeeSalariesController(EmployeeSalaryService salaries) : ControllerBase
{
    [HttpGet]
    [RequirePermission(PermissionFunctions.PayRules, PermissionAction.View, PermissionScope.AnyZone)]
    [ProducesResponseType<EmployeeSalaryView>(StatusCodes.Status200OK)]
    public async Task<IActionResult> Get(Guid id, CancellationToken cancellationToken) =>
        (await salaries.GetAsync(id, cancellationToken)).ToActionResult(Ok);

    /// <summary>A new setup from its date; the date of an existing setup replaces it. A date inside a finalized payroll is MSG-LABOR-04.</summary>
    [HttpPost]
    [RequirePermission(PermissionFunctions.PayRules, PermissionAction.Edit, PermissionScope.AnyZone)]
    [ProducesResponseType<EmployeeSalaryView>(StatusCodes.Status200OK)]
    public async Task<IActionResult> Change(Guid id, ChangeSalaryRequest request, CancellationToken cancellationToken) =>
        (await salaries.ChangeAsync(id, request, cancellationToken)).ToActionResult(Ok);
}
