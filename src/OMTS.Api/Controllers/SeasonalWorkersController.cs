using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using OMTS.Api.Common;
using OMTS.Api.Filters;
using OMTS.Application.Features.AccessManagement.Dtos;
using OMTS.Application.Features.Authorization;
using OMTS.Application.Features.Authorization.Models;
using OMTS.Application.Features.Workforce;
using OMTS.Application.Features.Workforce.Dtos;
using OMTS.Domain.Constants;
using static OMTS.Application.Features.Authorization.Models.PermissionAction;
using static OMTS.Application.Features.Authorization.Models.PermissionScope;

namespace OMTS.Api.Controllers;

/// <summary>
/// LAB-02 – seasonal workers (no login accounts), cooperative-wide: a LABOR_WORKERS grant in any zone is enough. Phone and
/// citizen ID are returned only here (LABOR_WORKERS viewers, the Accountant with PAYROLL View, the Owner).
/// </summary>
[ApiController]
[Route("api/labor/workers")]
[Authorize]
[ProducesResponseType<ProblemDetails>(StatusCodes.Status400BadRequest)]
[ProducesResponseType<ProblemDetails>(StatusCodes.Status403Forbidden)]
[ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
[ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict)]
public sealed class SeasonalWorkersController(SeasonalWorkerService workers) : ControllerBase
{
    /// <summary>Search by name, phone or citizen ID; filter by <c>teamId</c> and <c>statusId</c> (101 In Use, 102 Discontinued).</summary>
    [HttpGet]
    [RequireEitherPermission(PermissionFunctions.LaborWorkers, View, AnyZone, PermissionFunctions.Payroll, View, AllZones)]
    [ProducesResponseType<Paged<WorkerListItem>>(StatusCodes.Status200OK)]
    public async Task<IActionResult> Search(
        string? search, Guid? teamId, int? statusId, int page = 1, int pageSize = 20, CancellationToken cancellationToken = default) =>
        Ok(await workers.SearchAsync(search, teamId, statusId, page, pageSize, cancellationToken));

    [HttpGet("{id:guid}")]
    [RequireEitherPermission(PermissionFunctions.LaborWorkers, View, AnyZone, PermissionFunctions.Payroll, View, AllZones)]
    [ProducesResponseType<WorkerView>(StatusCodes.Status200OK)]
    public async Task<IActionResult> Get(Guid id, CancellationToken cancellationToken) =>
        (await workers.GetAsync(id, cancellationToken)).ToActionResult(Ok);

    /// <summary>
    /// Example: <c>{"fullName":"Trần Văn Bé","phoneNumber":"0912345678","teamId":null}</c>. Phone or citizen ID is required;
    /// the same name + phone or citizen ID answers 409 MSG03 with <c>existingId</c>.
    /// </summary>
    [HttpPost]
    [RequirePermission(PermissionFunctions.LaborWorkers, PermissionAction.Create, AnyZone)]
    [ProducesResponseType<WorkerView>(StatusCodes.Status201Created)]
    public async Task<IActionResult> Create(SaveWorkerRequest request, CancellationToken cancellationToken) =>
        (await workers.CreateAsync(request, cancellationToken)).ToActionResult(w => StatusCode(StatusCodes.Status201Created, w));

    /// <summary>Changing the team affects only future attendance; send the <c>rowVersion</c> read (MSG06).</summary>
    [HttpPut("{id:guid}")]
    [RequirePermission(PermissionFunctions.LaborWorkers, Edit, AnyZone)]
    [ProducesResponseType<WorkerView>(StatusCodes.Status200OK)]
    public async Task<IActionResult> Update(Guid id, SaveWorkerRequest request, CancellationToken cancellationToken) =>
        (await workers.UpdateAsync(id, request, cancellationToken)).ToActionResult(Ok);

    /// <summary>In Use → Discontinued: no new work or attendance; history unchanged. Never blocked.</summary>
    [HttpPost("{id:guid}/deactivate")]
    [RequirePermission(PermissionFunctions.LaborWorkers, Edit, AnyZone)]
    [ProducesResponseType<WorkerView>(StatusCodes.Status200OK)]
    public async Task<IActionResult> Deactivate(Guid id, CancellationToken cancellationToken) =>
        (await workers.ChangeStatusAsync(id, StatusIds.Usage.Discontinued, cancellationToken)).ToActionResult(Ok);

    [HttpPost("{id:guid}/reactivate")]
    [RequirePermission(PermissionFunctions.LaborWorkers, Edit, AnyZone)]
    [ProducesResponseType<WorkerView>(StatusCodes.Status200OK)]
    public async Task<IActionResult> Reactivate(Guid id, CancellationToken cancellationToken) =>
        (await workers.ChangeStatusAsync(id, StatusIds.Usage.InUse, cancellationToken)).ToActionResult(Ok);
}
