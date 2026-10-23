using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using OMTS.Api.Common;
using OMTS.Api.Filters;
using OMTS.Application.Features.Authorization;
using OMTS.Application.Features.Authorization.Models;
using OMTS.Application.Features.LaborCost;
using OMTS.Application.Features.LaborCost.Dtos;
using static OMTS.Application.Features.Authorization.Models.PermissionAction;
using static OMTS.Application.Features.Authorization.Models.PermissionScope;

namespace OMTS.Api.Controllers;

/// <summary>
/// LAB-07 prepare payroll (Accountant with PAYROLL at All zones, or the Owner) and LAB-08 finalize, send back and reopen
/// (Cooperative Owner only). <c>period</c> is "YYYY-MM"; finalizing locks the work, attendance and pay of the month.
/// </summary>
[ApiController]
[Route("api/payrolls")]
[Authorize]
[ProducesResponseType<ProblemDetails>(StatusCodes.Status400BadRequest)]
[ProducesResponseType<ProblemDetails>(StatusCodes.Status403Forbidden)]
[ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
[ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict)]
public sealed class PayrollsController(
    PayrollPreparationService preparation,
    PayrollFinalizationService finalization,
    PayrollQueryService queries) : ControllerBase
{
    /// <summary>Months with totals and the derived state (Draft, Sent back, Finalized, Fully paid); <c>year</c> optional.</summary>
    [HttpGet]
    [RequirePermission(PermissionFunctions.Payroll, View, AllZones)]
    [ProducesResponseType<IReadOnlyList<PayrollListItem>>(StatusCodes.Status200OK)]
    public async Task<IActionResult> List(int? year, CancellationToken cancellationToken) =>
        Ok(await queries.ListAsync(year, cancellationToken));

    /// <summary>Example: <c>{"period":"2026-10"}</c>. Creates the Draft payslips; a month that already has a payroll answers MSG03.</summary>
    [HttpPost]
    [RequirePermission(PermissionFunctions.Payroll, PermissionAction.Create, AllZones)]
    [ProducesResponseType<PayrollDetail>(StatusCodes.Status201Created)]
    public async Task<IActionResult> Create(CreatePayrollRequest request, CancellationToken cancellationToken) =>
        (await preparation.CreateAsync(request, cancellationToken)).ToActionResult(p => StatusCode(StatusCodes.Status201Created, p));

    /// <summary>Payslips with lines, gross, deductions, net, missing-rate flags and the return reason.</summary>
    [HttpGet("{period}")]
    [RequirePermission(PermissionFunctions.Payroll, View, AllZones)]
    [ProducesResponseType<PayrollDetail>(StatusCodes.Status200OK)]
    public async Task<IActionResult> Get(string period, CancellationToken cancellationToken) =>
        (await queries.GetAsync(period, cancellationToken)).ToActionResult(Ok);

    /// <summary>Recalculates the system lines from attendance, piecework, rates and advances; manual lines are kept.</summary>
    [HttpPost("{period}/refresh")]
    [RequirePermission(PermissionFunctions.Payroll, Edit, AllZones)]
    [ProducesResponseType<PayrollDetail>(StatusCodes.Status200OK)]
    public async Task<IActionResult> Refresh(string period, CancellationToken cancellationToken) =>
        (await preparation.RefreshAsync(period, cancellationToken)).ToActionResult(Ok);

    /// <summary>Example: <c>{"type":"Addition","amount":200000,"reason":"Thưởng chuyên cần"}</c> (Addition or Deduction).</summary>
    [HttpPost("{period}/payslips/{payslipId:guid}/lines")]
    [RequirePermission(PermissionFunctions.Payroll, Edit, AllZones)]
    [ProducesResponseType<PayslipView>(StatusCodes.Status201Created)]
    public async Task<IActionResult> AddManualLine(string period, Guid payslipId, AddManualLineRequest request, CancellationToken cancellationToken) =>
        (await preparation.AddManualLineAsync(period, payslipId, request, cancellationToken)).ToActionResult(p => StatusCode(StatusCodes.Status201Created, p));

    /// <summary>Every payslip Draft → Approved; refused when the payroll is empty or a payslip has a missing rate (MSG50).</summary>
    [HttpPost("{period}/finalize")]
    [RequireCooperativeOwner]
    [ProducesResponseType<PayrollDetail>(StatusCodes.Status200OK)]
    public async Task<IActionResult> Finalize(string period, CancellationToken cancellationToken) =>
        (await finalization.FinalizeAsync(period, cancellationToken)).ToActionResult(Ok);

    /// <summary>Keeps the payslips Draft, stores the reason and notifies the preparer.</summary>
    [HttpPost("{period}/send-back")]
    [RequireCooperativeOwner]
    [ProducesResponseType<PayrollDetail>(StatusCodes.Status200OK)]
    public async Task<IActionResult> SendBack(string period, PayrollReasonRequest request, CancellationToken cancellationToken) =>
        (await finalization.SendBackAsync(period, request, cancellationToken)).ToActionResult(Ok);

    /// <summary>Every payslip Approved → Draft and the month unlocked; refused once a payment is recorded (MSG61).</summary>
    [HttpPost("{period}/reopen")]
    [RequireCooperativeOwner]
    [ProducesResponseType<PayrollDetail>(StatusCodes.Status200OK)]
    public async Task<IActionResult> Reopen(string period, PayrollReasonRequest request, CancellationToken cancellationToken) =>
        (await finalization.ReopenAsync(period, request, cancellationToken)).ToActionResult(Ok);
}
