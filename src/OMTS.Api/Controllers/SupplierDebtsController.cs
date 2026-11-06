using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using OMTS.Api.Common;
using OMTS.Api.Filters;
using OMTS.Application.Features.Authorization;
using OMTS.Application.Features.Authorization.Models;
using OMTS.Application.Features.Purchasing.Payments;

namespace OMTS.Api.Controllers;

/// <summary>
/// PURCHASE-04 – supplier debts, for the Owner and the Accountant (§I.4.4; the Zone Manager has no access, BR-FARM-07). They
/// have no zone: any Effective pair of a role holding SUPPLIER_PAYMENTS opens them (BR-FARM-08).
/// </summary>
[ApiController]
[Route("api/supplier-debts")]
[Authorize]
public sealed class SupplierDebtsController(SupplierDebtService debts, SupplierDebtExportService exports) : ControllerBase
{
    /// <summary>Step 1: the suppliers whose debt today is not zero, with its age; MSG-GEN-10 in <c>message</c> when there is none.</summary>
    [HttpGet]
    [RequirePermission(PermissionFunctions.SupplierPayments, PermissionAction.View, PermissionScope.NoZone)]
    [ProducesResponseType<SupplierDebtList>(StatusCodes.Status200OK)]
    public async Task<IActionResult> List(string? search, int page = 1, int pageSize = 20, CancellationToken cancellationToken = default) =>
        Ok(await debts.ListAsync(search, page, pageSize, cancellationToken));

    /// <summary>PURCHASE-04.2: the debt list and, with <c>supplierId</c>, its statement of the period (default: the current month).</summary>
    [HttpGet("export")]
    [RequirePermission(PermissionFunctions.SupplierPayments, PermissionAction.Export, PermissionScope.NoZone)]
    public async Task<IActionResult> Export([FromQuery] SupplierDebtExportQuery query, CancellationToken cancellationToken) =>
        (await exports.ExportAsync(query, cancellationToken)).ToActionResult(f => File(f.Content, f.ContentType, f.FileName));

    /// <summary>The debt today and the unpaid receipts of any supplier, oldest first (PURCHASE-05 step 1).</summary>
    [HttpGet("{supplierId:guid}")]
    [RequirePermission(PermissionFunctions.SupplierPayments, PermissionAction.View, PermissionScope.NoZone)]
    [ProducesResponseType<SupplierDebtDetail>(StatusCodes.Status200OK)]
    public async Task<IActionResult> Get(Guid supplierId, CancellationToken cancellationToken) =>
        (await debts.GetAsync(supplierId, cancellationToken)).ToActionResult(Ok);

    /// <summary>Step 3: debt at the start, receipts, returns and payments in date order with the debt after each, debt at the end.</summary>
    [HttpGet("{supplierId:guid}/statement")]
    [RequirePermission(PermissionFunctions.SupplierPayments, PermissionAction.View, PermissionScope.NoZone)]
    [ProducesResponseType<SupplierDebtStatement>(StatusCodes.Status200OK)]
    public async Task<IActionResult> Statement(Guid supplierId, DateOnly? from, DateOnly? to, CancellationToken cancellationToken) =>
        (await debts.StatementAsync(supplierId, from, to, cancellationToken)).ToActionResult(Ok);
}
