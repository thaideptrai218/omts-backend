using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using OMTS.Api.Common;
using OMTS.Api.Filters;
using OMTS.Application.Features.Authorization;
using OMTS.Application.Features.Authorization.Models;
using OMTS.Application.Features.Purchasing.Payments;

namespace OMTS.Api.Controllers;

/// <summary>
/// PURCHASE-05 – supplier payments, money paid back and their cancellation. They have no zone, so they follow the role only
/// (BR-FARM-08): any Effective pair of a role holding SUPPLIER_PAYMENTS (the Accountant), whatever its zone, or the Owner.
/// </summary>
[ApiController]
[Route("api/supplier-payments")]
[Authorize]
public sealed class SupplierPaymentsController(SupplierPaymentService payments) : ControllerBase
{
    [HttpGet]
    [RequirePermission(PermissionFunctions.SupplierPayments, PermissionAction.View, PermissionScope.NoZone)]
    [ProducesResponseType<SupplierPaymentPage>(StatusCodes.Status200OK)]
    public async Task<IActionResult> Search([FromQuery] SupplierPaymentQuery query, CancellationToken cancellationToken) =>
        Ok(await payments.SearchAsync(query, cancellationToken));

    [HttpGet("{id:guid}")]
    [RequirePermission(PermissionFunctions.SupplierPayments, PermissionAction.View, PermissionScope.NoZone)]
    [ProducesResponseType<SupplierPaymentView>(StatusCodes.Status200OK)]
    public async Task<IActionResult> Get(Guid id, CancellationToken cancellationToken) =>
        (await payments.GetAsync(id, cancellationToken)).ToActionResult(Ok);

    /// <summary>
    /// A larger amount than the debt answers 409 MSG-PURCHASE-05 until <c>confirmAdvance</c> is sent, and a bank reference
    /// already recorded 409 MSG-PURCHASE-06 until <c>confirmDuplicateBankReference</c> is. A repeated <c>deduplicationKey</c>
    /// returns the first payment and creates nothing.
    /// </summary>
    [HttpPost]
    [RequirePermission(PermissionFunctions.SupplierPayments, PermissionAction.Create, PermissionScope.NoZone)]
    [ProducesResponseType<SupplierPaymentView>(StatusCodes.Status201Created)]
    public async Task<IActionResult> Record(RecordSupplierPaymentRequest request, CancellationToken cancellationToken) =>
        (await payments.RecordAsync(request, cancellationToken)).ToActionResult(p => StatusCode(StatusCodes.Status201Created, p));

    /// <summary>PURCHASE-05.3: a payment recorded by mistake, with a reason; 409 MSG-GEN-09 when it is already cancelled.</summary>
    [HttpPost("{id:guid}/cancel")]
    [RequirePermission(PermissionFunctions.SupplierPayments, PermissionAction.Create, PermissionScope.NoZone)]
    [ProducesResponseType<SupplierPaymentView>(StatusCodes.Status200OK)]
    public async Task<IActionResult> Cancel(Guid id, CancelSupplierPaymentRequest request, CancellationToken cancellationToken) =>
        (await payments.CancelAsync(id, request, cancellationToken)).ToActionResult(Ok);
}
