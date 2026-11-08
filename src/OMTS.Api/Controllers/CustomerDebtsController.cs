using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using OMTS.Api.Common;
using OMTS.Api.Filters;
using OMTS.Application.Features.Authorization;
using OMTS.Application.Features.Authorization.Models;
using OMTS.Application.Features.Sales.Debts;

namespace OMTS.Api.Controllers;

/// <summary>
/// DEBT-03 – customer debts, for the Accountant and the Owner (§I.4.4; the Zone Manager and the trader have no access). They have no zone: any Effective pair of a role
/// holding CUSTOMER_DEBTS opens them (BR-FARM-08). Read only.
/// </summary>
[ApiController]
[Route("api/customer-debts")]
[Authorize]
public sealed class CustomerDebtsController(CustomerDebtService debts, CustomerDebtExportService exports) : ControllerBase
{
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
