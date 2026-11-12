using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using OMTS.Api.Common;
using OMTS.Api.Filters;
using OMTS.Application.Features.Authorization;
using OMTS.Application.Features.Authorization.Models;
using OMTS.Application.Features.Sales.Debts;
using OMTS.Application.Features.Sales.Payments;

namespace OMTS.Api.Controllers;

/// <summary>
/// DEBT-03 – customer debts, for the Accountant and the Owner (§I.4.4; the Zone Manager and the trader have no access). They have no zone: any Effective pair of a role
/// holding CUSTOMER_DEBTS opens them (BR-FARM-08). Read only.
/// </summary>
[ApiController]
[Route("api/customer-debts")]
[Authorize]
public sealed class CustomerDebtsController(CustomerDebtService debts, CustomerDebtExportService exports, CustomerDebtAdjustmentService adjustments) : ControllerBase
{
    /// <summary>
    /// The debt tab of the customer screen: the debt now, the unpaid invoices oldest first and the entries (invoice, price cut, payment, discount, money paid back, adjustment), newest
    /// first, each with the debt after it. <c>kinds</c> (repeat the parameter), <c>from</c> and <c>to</c> narrow the entries, never the debt after each.
    /// </summary>
    [HttpGet("{customerId:guid}")]
    [RequirePermission(PermissionFunctions.CustomerDebts, PermissionAction.View, PermissionScope.NoZone)]
    [ProducesResponseType<CustomerDebtTab>(StatusCodes.Status200OK)]
    public async Task<IActionResult> Tab(Guid customerId, [FromQuery] CustomerDebtTabQuery query, CancellationToken cancellationToken) =>
        (await debts.TabAsync(customerId, query, cancellationToken)).ToActionResult(Ok);

    /// <summary>A correction of the debt by hand ("Điều chỉnh"): a signed amount, above 0 the customer owes more. The Owner and the Accountant (CUSTOMER_PAYMENTS Edit).</summary>
    [HttpPost("adjustments")]
    [RequirePermission(PermissionFunctions.CustomerPayments, PermissionAction.Edit, PermissionScope.NoZone)]
    [ProducesResponseType<CustomerDebtAdjustmentSaved>(StatusCodes.Status201Created)]
    public async Task<IActionResult> Adjust(RecordCustomerDebtAdjustmentRequest request, CancellationToken cancellationToken) =>
        (await adjustments.RecordAsync(request, cancellationToken)).ToActionResult(a => StatusCode(StatusCodes.Status201Created, a));

    /// <summary>An adjustment made by mistake, with a reason; it no longer counts. 409 MSG-GEN-09 when it is already cancelled.</summary>
    [HttpPost("adjustments/{id:guid}/cancel")]
    [RequirePermission(PermissionFunctions.CustomerPayments, PermissionAction.Edit, PermissionScope.NoZone)]
    [ProducesResponseType<CustomerDebtAdjustmentSaved>(StatusCodes.Status200OK)]
    public async Task<IActionResult> CancelAdjustment(Guid id, CancelCustomerPaymentRequest request, CancellationToken cancellationToken) =>
        (await adjustments.CancelAsync(id, request, cancellationToken)).ToActionResult(Ok);

    /// <summary>Step 1: the profiles whose debt today is not 0, with the age of a debt above 0, the totals and the payments Awaiting confirmation.</summary>
    [HttpGet]
    [RequirePermission(PermissionFunctions.CustomerDebts, PermissionAction.View, PermissionScope.NoZone)]
    [ProducesResponseType<CustomerDebtList>(StatusCodes.Status200OK)]
    public async Task<IActionResult> List(string? search, int page = 1, int pageSize = 20, CancellationToken cancellationToken = default) =>
        Ok(await debts.ListAsync(search, page, pageSize, cancellationToken));

    /// <summary>DEBT-03.2: the debt list and, with <c>customerId</c>, its statement of the period (default: the current month) and its unpaid invoices.</summary>
    [HttpGet("export")]
    [RequirePermission(PermissionFunctions.CustomerDebts, PermissionAction.Export, PermissionScope.NoZone)]
    public async Task<IActionResult> Export([FromQuery] CustomerDebtExportQuery query, CancellationToken cancellationToken) =>
        (await exports.ExportAsync(query, cancellationToken)).ToActionResult(f => File(f.Content, f.ContentType, f.FileName));

    /// <summary>Step 3: debt at the start, invoices and payments in date order with the debt after each, debt at the end, and the invoices unpaid today.</summary>
    [HttpGet("{customerId:guid}/statement")]
    [RequirePermission(PermissionFunctions.CustomerDebts, PermissionAction.View, PermissionScope.NoZone)]
    [ProducesResponseType<CustomerDebtStatement>(StatusCodes.Status200OK)]
    public async Task<IActionResult> Statement(Guid customerId, DateOnly? from, DateOnly? to, CancellationToken cancellationToken) =>
        (await debts.StatementAsync(customerId, from, to, cancellationToken)).ToActionResult(Ok);
}
