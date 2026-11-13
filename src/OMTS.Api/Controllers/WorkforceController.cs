using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using OMTS.Api.Common;
using OMTS.Api.Filters;
using OMTS.Application.Common;
using OMTS.Application.Features.Authorization;
using OMTS.Application.Features.Authorization.Models;
using OMTS.Application.Features.Labor.Work;

namespace OMTS.Api.Controllers;

/// <summary>
/// LABOR-02, LABOR-04 and LABOR-05 – work assignment, attendance and employee requests: the Zone Manager in the zone of the raft (a Staff
/// employee: in a working zone of the employee) and the Cooperative Owner (§I.4.4). Correcting attendance and reviewing requests are
/// Owner-only; a Zone Manager only reads the requests. The service checks the zone of the raft of each record.
/// </summary>
[ApiController]
[Route("api/workforce")]
[Authorize]
public sealed class WorkforceController(
    WorkAssignmentService assignments,
    AttendanceRecordingService recording,
    AttendanceCorrectionService corrections,
    EmployeeRequestService requests,
    WorkQueryService queries,
    TimeProvider timeProvider) : ControllerBase
{
    private DateOnly DayOrToday(DateOnly? day) => day ?? timeProvider.Today();

    /// <summary>The day board: every employee with work on the day on the rafts of the caller's zones, with the check-in and check-out. <c>day</c> defaults to today.</summary>
    [HttpGet("assignments")]
    [RequirePermission(PermissionFunctions.WorkAssignments, PermissionAction.View, PermissionScope.AnyZone)]
    [ProducesResponseType<IReadOnlyList<AssignmentView>>(StatusCodes.Status200OK)]
    public async Task<IActionResult> List(DateOnly? day, CancellationToken cancellationToken) =>
        (await queries.ListAssignmentsAsync(DayOrToday(day), cancellationToken)).ToActionResult(Ok);

    /// <summary>The job types In Use and the Employed employees of the caller's zones, with their leave and raft on <c>day</c>.</summary>
    [HttpGet("lookups")]
    [RequirePermission(PermissionFunctions.WorkAssignments, PermissionAction.View, PermissionScope.AnyZone)]
    [ProducesResponseType<WorkLookups>(StatusCodes.Status200OK)]
    public async Task<IActionResult> Lookups(DateOnly? day, CancellationToken cancellationToken) =>
        Ok(await queries.LookupsAsync(DayOrToday(day), cancellationToken));

    /// <summary>
    /// LABOR-02.0: one assignment per employee, for today or later. 409 MSG-LABOR-06 names the employees already on another raft (resend
    /// with <c>moveExisting</c>); 409 MSG-LABOR-07 an employee already checked in.
    /// </summary>
    [HttpPost("assignments")]
    [RequirePermission(PermissionFunctions.WorkAssignments, PermissionAction.Create, PermissionScope.AnyZone)]
    [ProducesResponseType<IReadOnlyList<AssignmentView>>(StatusCodes.Status201Created)]
    public async Task<IActionResult> Assign(AssignWorkRequest request, CancellationToken cancellationToken) =>
        (await assignments.AssignAsync(request, cancellationToken)).ToActionResult(rows => StatusCode(StatusCodes.Status201Created, rows));

    /// <summary>LABOR-02.2: the raft or the job type of an assignment, before the employee is checked in.</summary>
    [HttpPut("assignments/{id:guid}")]
    [RequirePermission(PermissionFunctions.WorkAssignments, PermissionAction.Edit, PermissionScope.AnyZone)]
    [ProducesResponseType<AssignmentView>(StatusCodes.Status200OK)]
    public async Task<IActionResult> Edit(Guid id, EditAssignmentRequest request, CancellationToken cancellationToken) =>
        (await assignments.EditAsync(id, request, cancellationToken)).ToActionResult(Ok);

    /// <summary>LABOR-02.2: removes an assignment, before the employee is checked in. Ask MSG-GEN-04 first.</summary>
    [HttpDelete("assignments/{id:guid}")]
    [RequirePermission(PermissionFunctions.WorkAssignments, PermissionAction.Delete, PermissionScope.AnyZone)]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    public async Task<IActionResult> Remove(Guid id, CancellationToken cancellationToken) =>
        (await assignments.RemoveAsync(id, cancellationToken)).ToActionResult(_ => NoContent());

    /// <summary>LABOR-02.1: Change job for crew, today; answers 204.</summary>
    [HttpPost("crew-job")]
    [RequirePermission(PermissionFunctions.WorkAssignments, PermissionAction.Edit, PermissionScope.AnyZone)]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    public async Task<IActionResult> ChangeCrewJob(CrewJobRequest request, CancellationToken cancellationToken) =>
        (await assignments.ChangeCrewJobAsync(request, cancellationToken)).ToActionResult(_ => NoContent());

    /// <summary>LABOR-04.0: the check-in of the employee of the assignment, today. <c>qrCode</c> is the scanned code, or null to mark it by hand.</summary>
    [HttpPost("assignments/{id:guid}/check-in")]
    [RequirePermission(PermissionFunctions.Attendance, PermissionAction.Create, PermissionScope.AnyZone)]
    [ProducesResponseType<AssignmentView>(StatusCodes.Status200OK)]
    public async Task<IActionResult> CheckIn(Guid id, RecordAttendanceRequest request, CancellationToken cancellationToken) =>
        (await recording.CheckInAsync(id, request, cancellationToken)).ToActionResult(Ok);

    /// <summary>LABOR-04.1: the check-out; without a check-in it is saved with the warning MSG-LABOR-12 in <c>messageCode</c>.</summary>
    [HttpPost("assignments/{id:guid}/check-out")]
    [RequirePermission(PermissionFunctions.Attendance, PermissionAction.Create, PermissionScope.AnyZone)]
    [ProducesResponseType<AssignmentView>(StatusCodes.Status200OK)]
    public async Task<IActionResult> CheckOut(Guid id, RecordAttendanceRequest request, CancellationToken cancellationToken) =>
        (await recording.CheckOutAsync(id, request, cancellationToken)).ToActionResult(Ok);

    /// <summary>LABOR-04.8: the check-in of the selected members of a raft at the start time of the crew, with the reason.</summary>
    [HttpPost("crew-start")]
    [RequirePermission(PermissionFunctions.Attendance, PermissionAction.Create, PermissionScope.AnyZone)]
    [ProducesResponseType<IReadOnlyList<AssignmentView>>(StatusCodes.Status200OK)]
    public async Task<IActionResult> CrewStart(CrewStartRequest request, CancellationToken cancellationToken) =>
        (await recording.CrewStartAsync(request, cancellationToken)).ToActionResult(Ok);

    /// <summary>LABOR-04.7: the Staff employees of the caller's zones with their check-in and check-out on <c>day</c> (default today).</summary>
    [HttpGet("staff-attendance")]
    [RequirePermission(PermissionFunctions.Attendance, PermissionAction.View, PermissionScope.AnyZone)]
    [ProducesResponseType<IReadOnlyList<StaffAttendanceView>>(StatusCodes.Status200OK)]
    public async Task<IActionResult> Staff(DateOnly? day, CancellationToken cancellationToken) =>
        Ok(await queries.StaffAsync(DayOrToday(day), cancellationToken));

    [HttpPost("staff/{employeeId:guid}/check-in")]
    [RequirePermission(PermissionFunctions.Attendance, PermissionAction.Create, PermissionScope.AnyZone)]
    [ProducesResponseType<StaffAttendanceView>(StatusCodes.Status200OK)]
    public async Task<IActionResult> StaffCheckIn(Guid employeeId, RecordAttendanceRequest request, CancellationToken cancellationToken) =>
        (await recording.StaffCheckInAsync(employeeId, request, cancellationToken)).ToActionResult(Ok);

    [HttpPost("staff/{employeeId:guid}/check-out")]
    [RequirePermission(PermissionFunctions.Attendance, PermissionAction.Create, PermissionScope.AnyZone)]
    [ProducesResponseType<StaffAttendanceView>(StatusCodes.Status200OK)]
    public async Task<IActionResult> StaffCheckOut(Guid employeeId, RecordAttendanceRequest request, CancellationToken cancellationToken) =>
        (await recording.StaffCheckOutAsync(employeeId, request, cancellationToken)).ToActionResult(Ok);

    /// <summary>LABOR-04.4: the attendance and job periods of an employee on a day, for the correction. Cooperative Owner only.</summary>
    [HttpGet("attendance/{employeeId:guid}/{day}")]
    [RequireCooperativeOwner]
    [ProducesResponseType<AttendanceDayView>(StatusCodes.Status200OK)]
    public async Task<IActionResult> GetDay(Guid employeeId, DateOnly day, CancellationToken cancellationToken) =>
        (await corrections.GetDayAsync(employeeId, day, cancellationToken)).ToActionResult(Ok);

    /// <summary>LABOR-04.4: corrects the check-in, the check-out and the job periods of a day with a reason. Cooperative Owner only. Ask MSG-GEN-04 first.</summary>
    [HttpPut("attendance/{employeeId:guid}/{day}")]
    [RequireCooperativeOwner]
    [ProducesResponseType<AttendanceCorrected>(StatusCodes.Status200OK)]
    public async Task<IActionResult> Correct(Guid employeeId, DateOnly day, CorrectAttendanceRequest request, CancellationToken cancellationToken) =>
        (await corrections.CorrectAsync(employeeId, day, request, cancellationToken)).ToActionResult(Ok);

    /// <summary>
    /// SCR-LABOR-03: the requests of every employee, Awaiting review first and oldest first, then the others (at most 500). <c>statusId</c>
    /// (3801 Awaiting review, 3802 Approved, 3803 Rejected, 3804 Cancelled) shows one status. A Zone Manager reads them and decides none.
    /// </summary>
    [HttpGet("leave-requests")]
    [RequirePermission(PermissionFunctions.WorkAssignments, PermissionAction.View, PermissionScope.AnyZone)]
    [ProducesResponseType<IReadOnlyList<EmployeeRequestView>>(StatusCodes.Status200OK)]
    public async Task<IActionResult> Requests(int? statusId, CancellationToken cancellationToken) =>
        (await queries.ListRequestsAsync(statusId, cancellationToken)).ToActionResult(Ok);

    /// <summary>LABOR-05: approves or rejects (the note is then mandatory) a request Awaiting review. Cooperative Owner only.</summary>
    [HttpPost("leave-requests/{id:guid}/review")]
    [RequireCooperativeOwner]
    [ProducesResponseType<EmployeeRequestView>(StatusCodes.Status200OK)]
    public async Task<IActionResult> Review(Guid id, ReviewEmployeeRequest request, CancellationToken cancellationToken) =>
        (await requests.ReviewAsync(id, request, cancellationToken)).ToActionResult(Ok);
}
