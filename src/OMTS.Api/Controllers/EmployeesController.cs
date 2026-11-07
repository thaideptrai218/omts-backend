using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using OMTS.Api.Common;
using OMTS.Api.Filters;
using OMTS.Application.Features.AccessManagement.Dtos;
using OMTS.Application.Features.Authorization;
using OMTS.Application.Features.Authorization.Models;
using OMTS.Application.Features.Employees;

namespace OMTS.Api.Controllers;

/// <summary>
/// LABOR-01 – employee profiles: the Cooperative Owner, and a Zone Manager (§I.4.4). Every Zone Manager sees every employee;
/// the service checks that they maintain the one they change (a shared working zone, BR-LABOR-01).
/// </summary>
[ApiController]
[Route("api/employees")]
[Authorize]
public sealed class EmployeesController(
    EmployeeService employees, EmployeeQueryService queries, EmployeeWorkingZoneService workingZones, EmploymentService employment) : ControllerBase
{
    [HttpGet]
    [RequirePermission(PermissionFunctions.LaborWorkers, PermissionAction.View, PermissionScope.AnyZone)]
    [ProducesResponseType<Paged<EmployeeListItem>>(StatusCodes.Status200OK)]
    public async Task<IActionResult> Search(
        string? search, int? statusId, Guid? zoneId, int page = 1, int pageSize = 20, CancellationToken cancellationToken = default) =>
        Ok(await queries.SearchAsync(search, statusId, zoneId, page, pageSize, cancellationToken));

    [HttpGet("{id:guid}")]
    [RequirePermission(PermissionFunctions.LaborWorkers, PermissionAction.View, PermissionScope.AnyZone)]
    [ProducesResponseType<EmployeeView>(StatusCodes.Status200OK)]
    public async Task<IActionResult> Get(Guid id, CancellationToken cancellationToken) =>
        (await queries.GetAsync(id, cancellationToken)).ToActionResult(Ok);

    /// <summary>
    /// A Zone Manager sends <c>ownerApproved</c>: true (MSG-LABOR-01 otherwise). With an optional <c>account</c> block, for the
    /// Cooperative Owner only, the staff account is created in the same save; its temporary password is returned once.
    /// </summary>
    [HttpPost]
    [RequirePermission(PermissionFunctions.LaborWorkers, PermissionAction.Create, PermissionScope.AnyZone)]
    [ProducesResponseType<EmployeeSaved>(StatusCodes.Status201Created)]
    public async Task<IActionResult> Create(SaveEmployeeRequest request, CancellationToken cancellationToken) =>
        (await employees.CreateAsync(request, cancellationToken)).ToActionResult(e => StatusCode(StatusCodes.Status201Created, e));

    [HttpPut("{id:guid}")]
    [RequirePermission(PermissionFunctions.LaborWorkers, PermissionAction.Edit, PermissionScope.AnyZone)]
    [ProducesResponseType<EmployeeSaved>(StatusCodes.Status200OK)]
    public async Task<IActionResult> Update(Guid id, SaveEmployeeRequest request, CancellationToken cancellationToken) =>
        (await employees.UpdateAsync(id, request, cancellationToken)).ToActionResult(Ok);

    /// <summary>LABOR-01.2: adds one of the caller's zones to any Employed employee.</summary>
    [HttpPost("{id:guid}/working-zones")]
    [RequirePermission(PermissionFunctions.LaborWorkers, PermissionAction.Edit, PermissionScope.AnyZone)]
    [ProducesResponseType<EmployeeSaved>(StatusCodes.Status200OK)]
    public async Task<IActionResult> AddWorkingZone(Guid id, AddWorkingZoneRequest request, CancellationToken cancellationToken) =>
        (await workingZones.AddAsync(id, request, cancellationToken)).ToActionResult(Ok);

    /// <summary>The Cooperative Owner creates the staff account of an Employed employee who has none.</summary>
    [HttpPost("{id:guid}/account")]
    [RequireCooperativeOwner]
    [ProducesResponseType<EmployeeSaved>(StatusCodes.Status201Created)]
    public async Task<IActionResult> CreateAccount(Guid id, NewEmployeeAccount request, CancellationToken cancellationToken) =>
        (await employees.CreateAccountAsync(id, request, cancellationToken)).ToActionResult(e => StatusCode(StatusCodes.Status201Created, e));

    /// <summary>LABOR-01.3: the employee is Left from the day after <c>lastWorkingDay</c>.</summary>
    [HttpPost("{id:guid}/end-employment")]
    [RequirePermission(PermissionFunctions.LaborWorkers, PermissionAction.Edit, PermissionScope.AnyZone)]
    [ProducesResponseType<EmploymentChanged>(StatusCodes.Status200OK)]
    public async Task<IActionResult> EndEmployment(Guid id, EndEmploymentRequest request, CancellationToken cancellationToken) =>
        (await employment.EndEmploymentAsync(id, request, cancellationToken)).ToActionResult(Ok);

    /// <summary>LABOR-01.4: a Left employee is Employed again from today.</summary>
    [HttpPost("{id:guid}/resume")]
    [RequirePermission(PermissionFunctions.LaborWorkers, PermissionAction.Edit, PermissionScope.AnyZone)]
    [ProducesResponseType<EmploymentChanged>(StatusCodes.Status200OK)]
    public async Task<IActionResult> Resume(Guid id, ResumeEmployeeRequest request, CancellationToken cancellationToken) =>
        (await employment.ResumeAsync(id, request, cancellationToken)).ToActionResult(Ok);
}
