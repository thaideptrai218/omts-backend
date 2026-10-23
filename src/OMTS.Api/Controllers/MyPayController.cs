using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using OMTS.Api.Common;
using OMTS.Application.Features.LaborCost;
using OMTS.Application.Features.LaborCost.Dtos;

namespace OMTS.Api.Controllers;

/// <summary>
/// LAB-10 – the caller's own attendance, finalized payslips and advances (any employee account, the Owner included),
/// read-only. An account not linked to an employee answers 409 MSG51; an empty list carries the MSG08 text.
/// </summary>
[ApiController]
[Route("api/me/pay")]
[Authorize]
[ProducesResponseType<ProblemDetails>(StatusCodes.Status400BadRequest)]
[ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
[ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict)]
public sealed class MyPayController(PersonalPayService personalPay) : ControllerBase
{
    /// <summary><c>from</c>/<c>to</c> (default: this month, at most 366 days).</summary>
    [HttpGet("attendance")]
    [ProducesResponseType<MyList<MyAttendanceItem>>(StatusCodes.Status200OK)]
    public async Task<IActionResult> Attendance(DateOnly? from, DateOnly? to, CancellationToken cancellationToken) =>
        (await personalPay.AttendanceAsync(from, to, cancellationToken)).ToActionResult(Ok);

    /// <summary>Approved and Fully Paid payslips of <c>year</c> (default: this year); Draft figures are never returned.</summary>
    [HttpGet("payslips")]
    [ProducesResponseType<MyList<MyPayslipListItem>>(StatusCodes.Status200OK)]
    public async Task<IActionResult> Payslips(int? year, CancellationToken cancellationToken) =>
        (await personalPay.PayslipsAsync(year, cancellationToken)).ToActionResult(Ok);

    /// <summary>Lines, payments and remaining of an own finalized payslip; any other id is not found.</summary>
    [HttpGet("payslips/{id:guid}")]
    [ProducesResponseType<PayslipView>(StatusCodes.Status200OK)]
    public async Task<IActionResult> Payslip(Guid id, CancellationToken cancellationToken) =>
        (await personalPay.PayslipAsync(id, cancellationToken)).ToActionResult(Ok);

    [HttpGet("advances")]
    [ProducesResponseType<MyList<AdvanceView>>(StatusCodes.Status200OK)]
    public async Task<IActionResult> Advances(CancellationToken cancellationToken) =>
        (await personalPay.AdvancesAsync(cancellationToken)).ToActionResult(Ok);
}
