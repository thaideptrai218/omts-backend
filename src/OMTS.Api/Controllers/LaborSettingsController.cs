using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using OMTS.Api.Common;
using OMTS.Api.Filters;
using OMTS.Application.Features.Labor;

namespace OMTS.Api.Controllers;

/// <summary>LABOR-15 and LABOR-16 – the attendance and payroll settings: the Cooperative Owner only (§I.4.4).</summary>
[ApiController]
[Route("api")]
[Authorize]
[RequireCooperativeOwner]
public sealed class LaborSettingsController(AttendanceSettingsService attendance, PayrollSettingsService payroll) : ControllerBase
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

    [HttpGet("payroll-settings")]
    [ProducesResponseType<PayrollSettingsView>(StatusCodes.Status200OK)]
    public async Task<IActionResult> GetPayroll(CancellationToken cancellationToken) =>
        (await payroll.GetAsync(cancellationToken)).ToActionResult(Ok);

    /// <summary>The next two pay periods under the settings being chosen, with the question MSG-LABOR-36. Nothing is saved.</summary>
    [HttpGet("payroll-settings/preview")]
    [ProducesResponseType<PayrollSettingsPreview>(StatusCodes.Status200OK)]
    public async Task<IActionResult> PreviewPayroll(int? startDay, bool? automaticPayroll, CancellationToken cancellationToken) =>
        (await payroll.PreviewAsync(startDay, automaticPayroll, cancellationToken)).ToActionResult(Ok);

    [HttpPut("payroll-settings")]
    [ProducesResponseType<PayrollSettingsSaved>(StatusCodes.Status200OK)]
    public async Task<IActionResult> SavePayroll(SavePayrollSettingsRequest request, CancellationToken cancellationToken) =>
        (await payroll.SaveAsync(request, cancellationToken)).ToActionResult(Ok);
}
