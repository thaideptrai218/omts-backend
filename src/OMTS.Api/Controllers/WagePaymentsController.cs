using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using OMTS.Api.Common;
using OMTS.Api.Filters;
using OMTS.Application.Features.AccessManagement.Dtos;
using OMTS.Application.Features.Authorization;
using OMTS.Application.Features.Authorization.Models;
using OMTS.Application.Features.LaborCost;
using OMTS.Application.Features.LaborCost.Dtos;
using static OMTS.Application.Features.Authorization.Models.PermissionAction;
using static OMTS.Application.Features.Authorization.Models.PermissionScope;

namespace OMTS.Api.Controllers;

/// <summary>
/// LAB-09 wage payments of finalized payslips, reversals and salary advances (Accountant with PAYROLL at All zones, or the
/// Owner). Payments are never deleted; Fully Paid is final.
/// </summary>
[ApiController]
[Authorize]
[ProducesResponseType<ProblemDetails>(StatusCodes.Status400BadRequest)]
[ProducesResponseType<ProblemDetails>(StatusCodes.Status403Forbidden)]
[ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
[ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict)]
public sealed class WagePaymentsController(
    WagePaymentService wagePayments,
    PaymentReversalService reversals,
    SalaryAdvanceService advances) : ControllerBase
{
    private const long MaxDocumentBytes = 27L * 1024 * 1024;

    /// <summary>Payables: <c>state</c> = unpaid (default), paid or all; optional <c>period</c> "YYYY-MM".</summary>
    [HttpGet("api/payslips")]
    [RequirePermission(PermissionFunctions.Payroll, View, AllZones)]
    [ProducesResponseType<IReadOnlyList<PayableItem>>(StatusCodes.Status200OK)]
    public async Task<IActionResult> Payables(string? state, string? period, CancellationToken cancellationToken) =>
        (await wagePayments.ListAsync(state, period, cancellationToken)).ToActionResult(Ok);

    [HttpGet("api/payslips/{id:guid}")]
    [RequirePermission(PermissionFunctions.Payroll, View, AllZones)]
    [ProducesResponseType<PayslipView>(StatusCodes.Status200OK)]
    public async Task<IActionResult> Payslip(Guid id, CancellationToken cancellationToken) =>
        (await wagePayments.GetAsync(id, cancellationToken)).ToActionResult(Ok);

    /// <summary>
    /// Example: <c>{"amountPaid":1500000,"paymentDate":"2026-11-05","method":"Cash"}</c>. Above the remaining → MSG45;
    /// equal to it → the payslip becomes Fully Paid.
    /// </summary>
    [HttpPost("api/payslips/{id:guid}/payments")]
    [RequirePermission(PermissionFunctions.Payroll, PermissionAction.Create, AllZones)]
    [ProducesResponseType<PaymentRecorded>(StatusCodes.Status201Created)]
    public async Task<IActionResult> Pay(Guid id, RecordPaymentRequest request, CancellationToken cancellationToken) =>
        (await wagePayments.RecordAsync(id, request, cancellationToken)).ToActionResult(p => StatusCode(StatusCodes.Status201Created, p));

    /// <summary>Pays the full remaining of every payslip with one date and method, all or nothing (<c>failures</c> lists refusals).</summary>
    [HttpPost("api/payslips/payments/bulk")]
    [RequirePermission(PermissionFunctions.Payroll, PermissionAction.Create, AllZones)]
    [ProducesResponseType<IReadOnlyList<PaymentRecorded>>(StatusCodes.Status201Created)]
    public async Task<IActionResult> PayBulk(BulkPaymentRequest request, CancellationToken cancellationToken) =>
        (await wagePayments.BulkAsync(request, cancellationToken)).ToActionResult(p => StatusCode(StatusCodes.Status201Created, p));

    /// <summary>Adds a negative payment linked to the original (reason required); payments and advances alike.</summary>
    [HttpPost("api/labor-payments/{id:guid}/reverse")]
    [RequirePermission(PermissionFunctions.Payroll, Edit, AllZones)]
    [ProducesResponseType<PaymentRecorded>(StatusCodes.Status201Created)]
    public async Task<IActionResult> Reverse(Guid id, ReversePaymentRequest request, CancellationToken cancellationToken) =>
        (await reversals.ReverseAsync(id, request, cancellationToken)).ToActionResult(p => StatusCode(StatusCodes.Status201Created, p));

    /// <summary>Multipart <c>files</c>: receipt or signature photos (JPEG/PNG, at most 5 MB each, 5 in all).</summary>
    [HttpPost("api/labor-payments/{id:guid}/documents")]
    [RequirePermission(PermissionFunctions.Payroll, PermissionAction.Create, AllZones)]
    [RequestSizeLimit(MaxDocumentBytes)]
    [Consumes("multipart/form-data")]
    [ProducesResponseType<PaymentView>(StatusCodes.Status200OK)]
    public async Task<IActionResult> AddDocuments(Guid id, [FromForm] List<IFormFile>? files, CancellationToken cancellationToken) =>
        (await reversals.AddDocumentsAsync(id, await files.ToIncomingFilesAsync(cancellationToken), cancellationToken)).ToActionResult(Ok);

    /// <summary>Advances, optionally only <c>outstanding=true</c> ones and of one <c>employeeId</c> or <c>workerId</c>; paged (<c>pageSize</c> at most 100, default 20).</summary>
    [HttpGet("api/salary-advances")]
    [RequirePermission(PermissionFunctions.Payroll, View, AllZones)]
    [ProducesResponseType<Paged<AdvanceView>>(StatusCodes.Status200OK)]
    public async Task<IActionResult> Advances(
        bool outstanding = false, Guid? employeeId = null, Guid? workerId = null, int page = 1, int pageSize = 20, CancellationToken cancellationToken = default) =>
        Ok(await advances.ListAsync(outstanding, employeeId, workerId, page, pageSize, cancellationToken));

    /// <summary>Example: <c>{"workerId":"…","amount":500000,"paymentDate":"2026-10-20","method":"Cash"}</c>.</summary>
    [HttpPost("api/salary-advances")]
    [RequirePermission(PermissionFunctions.Payroll, PermissionAction.Create, AllZones)]
    [ProducesResponseType<AdvanceView>(StatusCodes.Status201Created)]
    public async Task<IActionResult> RecordAdvance(RecordAdvanceRequest request, CancellationToken cancellationToken) =>
        (await advances.RecordAsync(request, cancellationToken)).ToActionResult(a => StatusCode(StatusCodes.Status201Created, a));
}
