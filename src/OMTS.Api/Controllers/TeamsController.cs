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

/// <summary>LAB-02 – teams (crews or contractor teams with a crew leader), cooperative-wide; the Contractor entity.</summary>
[ApiController]
[Route("api/labor/teams")]
[Authorize]
[ProducesResponseType<ProblemDetails>(StatusCodes.Status400BadRequest)]
[ProducesResponseType<ProblemDetails>(StatusCodes.Status403Forbidden)]
[ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
[ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict)]
public sealed class TeamsController(TeamService teams) : ControllerBase
{
    [HttpGet]
    [RequireEitherPermission(PermissionFunctions.LaborWorkers, View, AnyZone, PermissionFunctions.Payroll, View, AllZones)]
    [ProducesResponseType<Paged<TeamListItem>>(StatusCodes.Status200OK)]
    public async Task<IActionResult> Search(string? search, int? statusId, int page = 1, int pageSize = 20, CancellationToken cancellationToken = default) =>
        Ok(await teams.SearchAsync(search, statusId, page, pageSize, cancellationToken));

    /// <summary>Team detail with its members.</summary>
    [HttpGet("{id:guid}")]
    [RequireEitherPermission(PermissionFunctions.LaborWorkers, View, AnyZone, PermissionFunctions.Payroll, View, AllZones)]
    [ProducesResponseType<TeamView>(StatusCodes.Status200OK)]
    public async Task<IActionResult> Get(Guid id, CancellationToken cancellationToken) =>
        (await teams.GetAsync(id, cancellationToken)).ToActionResult(Ok);

    /// <summary>Example: <c>{"name":"Tổ Bè Nam","phoneNumber":"0912345678"}</c>. Names are unique ignoring case (MSG03).</summary>
    [HttpPost]
    [RequirePermission(PermissionFunctions.LaborWorkers, PermissionAction.Create, AnyZone)]
    [ProducesResponseType<TeamView>(StatusCodes.Status201Created)]
    public async Task<IActionResult> Create(SaveTeamRequest request, CancellationToken cancellationToken) =>
        (await teams.CreateAsync(request, cancellationToken)).ToActionResult(t => StatusCode(StatusCodes.Status201Created, t));

    /// <summary>The leader (<c>leaderWorkerId</c>) must be a current member of the team (MSG01 "Leader").</summary>
    [HttpPut("{id:guid}")]
    [RequirePermission(PermissionFunctions.LaborWorkers, Edit, AnyZone)]
    [ProducesResponseType<TeamView>(StatusCodes.Status200OK)]
    public async Task<IActionResult> Update(Guid id, SaveTeamRequest request, CancellationToken cancellationToken) =>
        (await teams.UpdateAsync(id, request, cancellationToken)).ToActionResult(Ok);

    /// <summary>In Use → Discontinued; 409 MSG11 listing the periods while a team payslip is not fully paid.</summary>
    [HttpPost("{id:guid}/deactivate")]
    [RequirePermission(PermissionFunctions.LaborWorkers, Edit, AnyZone)]
    [ProducesResponseType<TeamView>(StatusCodes.Status200OK)]
    public async Task<IActionResult> Deactivate(Guid id, CancellationToken cancellationToken) =>
        (await teams.ChangeStatusAsync(id, StatusIds.Usage.Discontinued, cancellationToken)).ToActionResult(Ok);

    [HttpPost("{id:guid}/reactivate")]
    [RequirePermission(PermissionFunctions.LaborWorkers, Edit, AnyZone)]
    [ProducesResponseType<TeamView>(StatusCodes.Status200OK)]
    public async Task<IActionResult> Reactivate(Guid id, CancellationToken cancellationToken) =>
        (await teams.ChangeStatusAsync(id, StatusIds.Usage.InUse, cancellationToken)).ToActionResult(Ok);
}
