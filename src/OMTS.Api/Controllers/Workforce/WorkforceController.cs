using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using OMTS.Api.Common;
using OMTS.Api.Filters;
using OMTS.Application.Features.Authorization.Models;
using OMTS.Application.Features.Authorization;
using OMTS.Application.Features.Workforce.Dtos;
using OMTS.Application.Features.Workforce;

namespace OMTS.Api.Controllers.Workforce;

/// <summary>Personal employee endpoints never accept an employee/account id from the client.</summary>
[ApiController]
[Authorize]
[Route("api/workforce")]
public sealed class WorkforceController(WorkforceService work, LeaveService leave, WorkforceSettingsService settings) : ControllerBase
{
    [HttpGet("lookups")]
    public async Task<IActionResult> Lookups(DateOnly? day, CancellationToken ct) => (await work.LookupsAsync(day, ct)).ToActionResult(Ok);

    [HttpGet("assignments")]
    public async Task<IActionResult> Assignments(DateOnly day, CancellationToken ct) =>
        (await work.ListAsync(day, ct)).ToActionResult(Ok);

    [HttpPost("assignments")]
    [RequirePermission(PermissionFunctions.WorkAssignments, PermissionAction.Create, PermissionScope.AnyZone)]
    public async Task<IActionResult> Assign(AssignWorkRequest input, CancellationToken ct) =>
        (await work.AssignAsync(input, ct)).ToActionResult(Ok);

    [HttpDelete("assignments/{id:guid}")]
    [RequirePermission(PermissionFunctions.WorkAssignments, PermissionAction.Delete, PermissionScope.AnyZone)]
    public async Task<IActionResult> Cancel(Guid id, CancellationToken ct) =>
        (await work.CancelAsync(id, ct)).ToActionResult(_ => NoContent());

    [HttpPost("crew-job")]
    [RequirePermission(PermissionFunctions.WorkAssignments, PermissionAction.Edit, PermissionScope.AnyZone)]
    public async Task<IActionResult> ChangeCrew(ChangeCrewRequest input, CancellationToken ct) =>
        (await work.ChangeCrewAsync(input, ct)).ToActionResult(_ => NoContent());

    [HttpPost("assignments/{id:guid}/check-in")]
    [RequirePermission(PermissionFunctions.Attendance, PermissionAction.Create, PermissionScope.AnyZone)]
    public async Task<IActionResult> CheckIn(Guid id, MarkAttendanceRequest input, CancellationToken ct) =>
        (await work.MarkAsync(id, false, input, ct)).ToActionResult(Ok);

    [HttpPost("assignments/{id:guid}/check-out")]
    [RequirePermission(PermissionFunctions.Attendance, PermissionAction.Create, PermissionScope.AnyZone)]
    public async Task<IActionResult> CheckOut(Guid id, MarkAttendanceRequest input, CancellationToken ct) =>
        (await work.MarkAsync(id, true, input, ct)).ToActionResult(Ok);

    [HttpGet("/api/me/workforce")]
    public async Task<IActionResult> Mine(DateOnly from, DateOnly to, CancellationToken ct) =>
        (await work.MineAsync(from, to, ct)).ToActionResult(Ok);

    [HttpPost("/api/me/attendance-qr")]
    public async Task<IActionResult> AttendanceQr(CancellationToken ct) =>
        (await work.IssueQrAsync(ct)).ToActionResult(Ok);

    [HttpPost("/api/me/leave-requests")]
    public async Task<IActionResult> RequestLeave(RequestLeave input, CancellationToken ct) =>
        (await leave.RequestAsync(input, ct)).ToActionResult(Ok);

    [HttpDelete("/api/me/leave-requests/{id:guid}")]
    public async Task<IActionResult> CancelLeave(Guid id, CancellationToken ct) =>
        (await leave.CancelAsync(id, ct)).ToActionResult(Ok);

    [HttpGet("leave-requests")]
    [RequireCooperativeOwner]
    public async Task<IActionResult> Leaves(CancellationToken ct) => Ok(await leave.ListAsync(ct));

    [HttpPost("leave-requests/{id:guid}/review")]
    [RequireCooperativeOwner]
    public async Task<IActionResult> Review(Guid id, ReviewLeave input, CancellationToken ct) =>
        (await leave.ReviewAsync(id, input, ct)).ToActionResult(Ok);

    [HttpGet("attendance-settings")]
    [RequireCooperativeOwner]
    public async Task<IActionResult> Settings(CancellationToken ct) => Ok(await settings.ListAsync(ct));

    [HttpPost("attendance-settings")]
    [RequireCooperativeOwner]
    public async Task<IActionResult> SaveSettings(SaveAttendanceSettings input, CancellationToken ct) =>
        (await settings.SaveAsync(input, ct)).ToActionResult(Ok);

    [HttpPost("job-types")]
    [RequireCooperativeOwner]
    public async Task<IActionResult> CreateJob(SaveJobType input, CancellationToken ct) =>
        (await settings.CreateJobAsync(input, ct)).ToActionResult(Ok);
}
