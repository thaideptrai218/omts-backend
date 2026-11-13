using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using OMTS.Api.Common;
using OMTS.Api.Filters;
using OMTS.Application.Features.Labor.Payrolls;

namespace OMTS.Api.Controllers;

/// <summary>
/// LABOR-12, LABOR-13 and LABOR-14 – payroll sheets, payslips and wage payments. The Cooperative Owner only, by a fixed rule on the account
/// (<see cref="RequireCooperativeOwnerAttribute"/>), never by a function permission a role could be given: anyone else gets 403 MSG-GEN-08.
/// Money is whole VND; dates are yyyy-MM-dd.
/// </summary>
[ApiController]
[Route("api")]
[Authorize]
[RequireCooperativeOwner]
public sealed class PayrollsController(
    PayrollService payrolls,
    PayrollQueryService queries,
    PayrollPaymentService paying,
    PayrollExportService exports) : ControllerBase
{
    /// <summary>
    /// The sheets with <c>summary</c> over the whole filtered set. <c>status</c> repeats (<c>Creating</c>, <c>Provisional</c>, <c>Finalized</c>,
    /// <c>Cancelled</c>; none is every status), <c>from</c>/<c>to</c> keep the sheets whose period shares a day with them, <c>sortBy</c> is code
    /// (default), name, from, total, paid or status, <c>sortDir</c> asc or desc (default for code).
    /// </summary>
    [HttpGet("payrolls")]
    [ProducesResponseType<PayrollSheetPage>(StatusCodes.Status200OK)]
    public async Task<IActionResult> List([FromQuery] PayrollListQuery query, CancellationToken cancellationToken) =>
        (await queries.ListAsync(query, cancellationToken)).ToActionResult(Ok);

    /// <summary>The list as filtered and sorted (the same parameters, no paging), as <c>payrolls-yyyyMMdd.xlsx</c>: at most 5000 sheets.</summary>
    [HttpGet("payrolls/export")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public async Task<IActionResult> ExportList([FromQuery] PayrollExportQuery query, CancellationToken cancellationToken) =>
        File(await exports.ExportListAsync(query, cancellationToken));

    [HttpGet("payrolls/{id:guid}")]
    [ProducesResponseType<PayrollSheetDetail>(StatusCodes.Status200OK)]
    public async Task<IActionResult> Get(Guid id, CancellationToken cancellationToken) =>
        (await queries.GetAsync(id, cancellationToken)).ToActionResult(Ok);

    [HttpGet("payrolls/{id:guid}/export")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public async Task<IActionResult> ExportSheet(Guid id, CancellationToken cancellationToken) =>
        File(await exports.ExportSheetAsync(id, cancellationToken));

    /// <summary>
    /// Makes the sheet and calculates its payslips in one step: 201 with the Provisional sheet. 409 when a sheet of the same period already pays one
    /// of the employees, 400 when the period only overlaps one, has no hours, is a Monthly period that is not a whole month, or starts after today.
    /// </summary>
    [HttpPost("payrolls")]
    [ProducesResponseType<PayrollSheetDetail>(StatusCodes.Status201Created)]
    public async Task<IActionResult> Create(CreatePayrollRequest request, CancellationToken cancellationToken) =>
        (await payrolls.CreateAsync(request, cancellationToken)).ToActionResult(sheet => StatusCode(StatusCodes.Status201Created, sheet));

    /// <summary>Recalculates a Provisional sheet: <c>All</c> the payslips, or <c>WorkingDaysOnly</c> the hours and pay of the payslips it has. 409 otherwise.</summary>
    [HttpPost("payrolls/{id:guid}/reload")]
    [ProducesResponseType<PayrollSheetDetail>(StatusCodes.Status200OK)]
    public async Task<IActionResult> Reload(Guid id, ReloadPayrollRequest request, CancellationToken cancellationToken) =>
        (await payrolls.ReloadAsync(id, request, cancellationToken)).ToActionResult(Ok);

    /// <summary>Provisional to Finalized with the month of the cost (yyyy-MM); locks the records of the period. 409 when not Provisional or requests await review, 400 MSG-LABOR-28 when not ready.</summary>
    [HttpPost("payrolls/{id:guid}/finalize")]
    [ProducesResponseType<PayrollSheetDetail>(StatusCodes.Status200OK)]
    public async Task<IActionResult> Finalize(Guid id, FinalizePayrollRequest request, CancellationToken cancellationToken) =>
        (await payrolls.FinalizeAsync(id, request, cancellationToken)).ToActionResult(Ok);

    /// <summary>Creating or Provisional to Cancelled; 409 for a Finalized or Cancelled sheet.</summary>
    [HttpPost("payrolls/{id:guid}/cancel")]
    [ProducesResponseType<PayrollSheetDetail>(StatusCodes.Status200OK)]
    public async Task<IActionResult> Cancel(Guid id, CancellationToken cancellationToken) =>
        (await payrolls.CancelAsync(id, cancellationToken)).ToActionResult(Ok);

    /// <summary>
    /// Pays payslips of a Finalized sheet: one wage payment and one cash voucher per line (201 with the vouchers); 409 when the sheet is not Finalized.
    /// The optional <c>Idempotency-Key</c> header makes a repeated request return the payments already saved.
    /// </summary>
    [HttpPost("payrolls/{id:guid}/payments")]
    [ProducesResponseType<PayrollPaymentResult>(StatusCodes.Status201Created)]
    public async Task<IActionResult> Pay(
        Guid id, PayPayrollRequest request, [FromHeader(Name = "Idempotency-Key")] string? idempotencyKey, CancellationToken cancellationToken) =>
        (await paying.PaySheetAsync(id, request, Key(idempotencyKey), cancellationToken)).ToActionResult(result => StatusCode(StatusCodes.Status201Created, result));

    /// <summary>The payments (wages and advances), newest first; <c>employeeId</c> keeps one employee's history.</summary>
    [HttpGet("payroll-payments")]
    [ProducesResponseType<PayrollPaymentPage>(StatusCodes.Status200OK)]
    public async Task<IActionResult> ListPayments([FromQuery] PayrollPaymentListQuery query, CancellationToken cancellationToken) =>
        (await queries.PaymentsAsync(query, cancellationToken)).ToActionResult(Ok);

    /// <summary>
    /// Pays an employee outside a sheet: the oldest Finalized payslips first (kind Wage), what is left over, or all of it when nothing is owed, an
    /// Advance that the next Finalized sheet of the employee deducts. 201 with the id of the first voucher.
    /// </summary>
    [HttpPost("payroll-payments")]
    [ProducesResponseType<StandalonePaymentResult>(StatusCodes.Status201Created)]
    public async Task<IActionResult> PayEmployee(
        StandalonePayrollPaymentRequest request, [FromHeader(Name = "Idempotency-Key")] string? idempotencyKey, CancellationToken cancellationToken) =>
        (await paying.PayEmployeeAsync(request, Key(idempotencyKey), cancellationToken)).ToActionResult(result => StatusCode(StatusCodes.Status201Created, result));

    /// <summary>Employees with something owed on Finalized sheets, or, with <c>includeAll</c>, every Employed employee too (owed 0 for those).</summary>
    [HttpGet("payrolls/payable-employees")]
    [ProducesResponseType<PayableEmployees>(StatusCodes.Status200OK)]
    public async Task<IActionResult> PayableEmployees(string? search, bool includeAll, CancellationToken cancellationToken) =>
        (await queries.PayableEmployeesAsync(search, includeAll, cancellationToken)).ToActionResult(Ok);

    /// <summary>The payslips of one employee, newest period first, with what is still owed on the Finalized ones and the advances not deducted yet. Sheets that are Cancelled are left out unless <c>status</c> asks for them.</summary>
    [HttpGet("employees/{employeeId:guid}/payslips")]
    [ProducesResponseType<EmployeeSlipPage>(StatusCodes.Status200OK)]
    public async Task<IActionResult> EmployeePayslips(Guid employeeId, [FromQuery] EmployeePayslipListQuery query, CancellationToken cancellationToken) =>
        (await queries.EmployeeSlipsAsync(employeeId, query, cancellationToken)).ToActionResult(Ok);

    /// <summary>One payslip with the pay by job type, its payments and the attendance of the period.</summary>
    [HttpGet("payroll-slips/{slipId:guid}")]
    [ProducesResponseType<PayrollSlipDetail>(StatusCodes.Status200OK)]
    public async Task<IActionResult> Slip(Guid slipId, CancellationToken cancellationToken) =>
        (await queries.GetSlipAsync(slipId, cancellationToken)).ToActionResult(Ok);

    private static string? Key(string? header) => string.IsNullOrWhiteSpace(header) ? null : header.Trim();

    /// <summary>A file answer that no cache may keep (payroll is personal data).</summary>
    private IActionResult File(OMTS.Application.Common.Results.Result<OMTS.Application.Features.Purchasing.Receipts.ExportedFile> result)
    {
        Response.Headers.CacheControl = "no-store";
        return result.ToActionResult(f => base.File(f.Content, f.ContentType, f.FileName));
    }
}
