using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using OMTS.Api.Common;
using OMTS.Api.Filters;
using OMTS.Application.Features.Authorization;
using OMTS.Application.Features.Authorization.Models;
using OMTS.Application.Features.Labor.WorkAssignments;

namespace OMTS.Api.Controllers;

/// <summary>
/// LABOR-02 – assign work: a Zone Manager for the rafts of their zones and the Cooperative Owner, not the Accountant (§I.4.4).
/// The routes name a raft or an assignment, so each service checks the zone of the raft (MSG-GEN-08); the work-day view and the list of
/// employees show what the caller's zones hold. Attendance (LABOR-04) and the employee app (LABOR-03) come with later PRs.
/// </summary>
[ApiController]
[Authorize]
public sealed class WorkAssignmentsController(
    WorkDayQueryService workDays,
    AssignableEmployeeQueryService assignable,
    WorkAssignmentService assign,
    WorkAssignmentEditService edits,
    CrewJobChangeService crewChanges) : ControllerBase
{
    /// <summary>The rafts In Use of the caller's zones with their crews on a date, and the Staff employees of those zones (SCR-LABOR-02).</summary>
    [HttpGet("api/work-days/{date}")]
    [RequirePermission(PermissionFunctions.WorkAssignments, PermissionAction.View, PermissionScope.AnyZone)]
    [ProducesResponseType<WorkDayView>(StatusCodes.Status200OK)]
    public async Task<IActionResult> WorkDay(DateOnly date, CancellationToken cancellationToken) =>
        (await workDays.GetAsync(date, cancellationToken)).ToActionResult(Ok);

    /// <summary>
    /// The Employed employees to choose from (LABOR-02.0 steps 5 and 6): those with a working zone among the caller's zones, those whose
    /// profile has <c>jobTypeId</c> first; with <c>search</c> (name or phone number) every Employed employee of the cooperative.
    /// </summary>
    [HttpGet("api/work-days/{date}/assignable-employees")]
    [RequirePermission(PermissionFunctions.WorkAssignments, PermissionAction.View, PermissionScope.AnyZone)]
    [ProducesResponseType<AssignableEmployees>(StatusCodes.Status200OK)]
    public async Task<IActionResult> ListAssignable(
        DateOnly date, Guid? raftId, Guid? jobTypeId, string? search, CancellationToken cancellationToken) =>
        (await assignable.ListAsync(date, raftId, jobTypeId, search, cancellationToken)).ToActionResult(Ok);

    /// <summary>
    /// Assigns a job type and this raft to the employees for a date, today or later (LABOR-02.0). 409 MSG-LABOR-07 for an employee already
    /// checked in; 409 MSG-LABOR-06 for one assigned to another raft, until the request is sent again with <c>moveConfirmed</c>: true.
    /// All or nothing. MSG-LABOR-04 when a pay period holds the date.
    /// </summary>
    [HttpPost("api/rafts/{raftId:guid}/work-assignments")]
    [RequirePermission(PermissionFunctions.WorkAssignments, PermissionAction.Create, PermissionScope.AnyZone)]
    [ProducesResponseType<WorkAssignmentsSaved>(StatusCodes.Status201Created)]
    public async Task<IActionResult> Assign(Guid raftId, AssignWorkRequest request, CancellationToken cancellationToken) =>
        (await assign.AssignAsync(raftId, request, cancellationToken)).ToActionResult(a => StatusCode(StatusCodes.Status201Created, a));

    /// <summary>Changes the raft or the job type of work assigned for today or later, until the employee is checked in (LABOR-02.2).</summary>
    [HttpPut("api/work-assignments/{id:guid}")]
    [RequirePermission(PermissionFunctions.WorkAssignments, PermissionAction.Edit, PermissionScope.AnyZone)]
    [ProducesResponseType<WorkAssignmentSaved>(StatusCodes.Status200OK)]
    public async Task<IActionResult> Edit(Guid id, EditWorkAssignmentRequest request, CancellationToken cancellationToken) =>
        (await edits.EditAsync(id, request, cancellationToken)).ToActionResult(Ok);

    /// <summary>Removes work assigned for today or later, until the employee is checked in (LABOR-02.2). Ask MSG-GEN-04 first. 204.</summary>
    /// <param name="id">The assignment.</param>
    /// <param name="rowVersion">The <c>rowVersion</c> the assignment was read with; an older one answers MSG06.</param>
    /// <param name="cancellationToken">Request aborted.</param>
    [HttpDelete("api/work-assignments/{id:guid}")]
    [RequirePermission(PermissionFunctions.WorkAssignments, PermissionAction.Delete, PermissionScope.AnyZone)]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    public async Task<IActionResult> Remove(Guid id, [FromQuery] string? rowVersion, CancellationToken cancellationToken) =>
        (await edits.RemoveAsync(id, rowVersion, cancellationToken)).ToActionResult(_ => NoContent());

    /// <summary>
    /// Change job for crew, today (LABOR-02.1): a new job period from now for the members checked in and not checked out, the assignment
    /// changed for those not checked in yet; no push. 409 MSG-GEN-09 when no selected member is checked in.
    /// </summary>
    [HttpPost("api/rafts/{raftId:guid}/crew-job-changes")]
    [RequirePermission(PermissionFunctions.WorkAssignments, PermissionAction.Edit, PermissionScope.AnyZone)]
    [ProducesResponseType<CrewJobChanged>(StatusCodes.Status200OK)]
    public async Task<IActionResult> ChangeJob(Guid raftId, CrewJobChangeRequest request, CancellationToken cancellationToken) =>
        (await crewChanges.ChangeAsync(raftId, request, cancellationToken)).ToActionResult(Ok);
}
