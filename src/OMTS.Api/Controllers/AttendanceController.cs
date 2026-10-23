using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using OMTS.Api.Common;
using OMTS.Api.Filters;
using OMTS.Application.Features.Authorization;
using OMTS.Application.Features.Authorization.Models;
using OMTS.Application.Features.Workforce;
using OMTS.Application.Features.Workforce.Dtos;
using OMTS.Application.Features.WorkTracking;
using OMTS.Application.Features.WorkTracking.Dtos;
using static OMTS.Application.Features.Authorization.Models.PermissionAction;

namespace OMTS.Api.Controllers;

/// <summary>
/// LAB-04 attendance sheet of one zone and day; staff need ATTENDANCE in the zone (View: sheet and picker, Create: first
/// entry and evidence, Edit: changing a saved record, with a reason). Hours are informational only.
/// </summary>
[ApiController]
[Route("api/zones/{zoneId:guid}/attendance")]
[Authorize]
[ProducesResponseType<ProblemDetails>(StatusCodes.Status400BadRequest)]
[ProducesResponseType<ProblemDetails>(StatusCodes.Status403Forbidden)]
[ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
[ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict)]
public sealed class AttendanceController(
    AttendanceSheetQuery sheets,
    AttendanceSheetService attendance,
    LaborPickerService pickers) : ControllerBase
{
    private const long MaxEvidenceBytes = 27L * 1024 * 1024;

    /// <summary>Saved records of <c>date</c> (yyyy-MM-dd) plus <c>suggested</c> rows from the day's active daily-wage assignments.</summary>
    [HttpGet]
    [RequirePermission(PermissionFunctions.Attendance, View)]
    [ProducesResponseType<AttendanceSheet>(StatusCodes.Status200OK)]
    public async Task<IActionResult> Get(Guid zoneId, DateOnly? date, CancellationToken cancellationToken) =>
        (await sheets.GetAsync(zoneId, date, cancellationToken)).ToActionResult(Ok);

    /// <summary>People picker: Employed employees working in the zone, In Use workers and teams (no personal data).</summary>
    [HttpGet("people")]
    [RequirePermission(PermissionFunctions.Attendance, View)]
    [ProducesResponseType<IReadOnlyList<LaborPickerItem>>(StatusCodes.Status200OK)]
    public async Task<IActionResult> People(Guid zoneId, string? search, CancellationToken cancellationToken) =>
        Ok(await pickers.ListAsync(zoneId, search, cancellationToken));

    /// <summary>
    /// Saves all rows in one transaction. Example: <c>{"rows":[{"teamId":"…","result":"Present","hours":8},
    /// {"workerId":"…","result":"UnpaidLeave"}]}</c> – the team row covers every In Use member, the individual row overrides it.
    /// One invalid row saves nothing; the problem lists every refused row in <c>rows</c>.
    /// </summary>
    [HttpPut("{date}")]
    [RequirePermission(PermissionFunctions.Attendance, PermissionAction.Create)]
    [ProducesResponseType<AttendanceSheet>(StatusCodes.Status200OK)]
    public async Task<IActionResult> Save(Guid zoneId, DateOnly date, SaveAttendanceRequest request, CancellationToken cancellationToken) =>
        (await attendance.SaveAsync(zoneId, date, request, cancellationToken)).ToActionResult(Ok);

    /// <summary>Multipart <c>files</c>: evidence photos of a saved record (JPEG/PNG, at most 5 MB each, 5 in all).</summary>
    [HttpPost("records/{recordId:guid}/evidence")]
    [RequirePermission(PermissionFunctions.Attendance, PermissionAction.Create)]
    [RequestSizeLimit(MaxEvidenceBytes)]
    [Consumes("multipart/form-data")]
    [ProducesResponseType<AttendanceSheetRow>(StatusCodes.Status200OK)]
    public async Task<IActionResult> AddEvidence(Guid zoneId, Guid recordId, [FromForm] List<IFormFile>? files, CancellationToken cancellationToken) =>
        (await attendance.AddEvidenceAsync(zoneId, recordId, await files.ToIncomingFilesAsync(cancellationToken), cancellationToken)).ToActionResult(Ok);
}
