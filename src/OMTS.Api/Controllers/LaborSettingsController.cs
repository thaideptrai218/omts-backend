using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using OMTS.Api.Common;
using OMTS.Api.Filters;
using OMTS.Application.Features.Labor;

namespace OMTS.Api.Controllers;

/// <summary>LABOR-15 – the attendance settings: the Cooperative Owner only (§I.4.4).</summary>
[ApiController]
[Route("api")]
[Authorize]
[RequireCooperativeOwner]
public sealed class LaborSettingsController(AttendanceSettingsService attendance) : ControllerBase
{
    [HttpGet("attendance-settings")]
    [ProducesResponseType<AttendanceSettingsOverview>(StatusCodes.Status200OK)]
    public async Task<IActionResult> GetAttendance(CancellationToken cancellationToken) =>
        (await attendance.GetAsync(cancellationToken)).ToActionResult(Ok);

    /// <summary>The settings from their date; the date of a saved version replaces it. Ask MSG-LABOR-35 first.</summary>
    [HttpPost("attendance-settings")]
    [ProducesResponseType<AttendanceSettingsSaved>(StatusCodes.Status200OK)]
    public async Task<IActionResult> SaveAttendance(SaveAttendanceSettingsRequest request, CancellationToken cancellationToken) =>
        (await attendance.SaveAsync(request, cancellationToken)).ToActionResult(Ok);
}
