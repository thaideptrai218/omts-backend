using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using OMTS.Api.Common;
using OMTS.Api.Filters;
using OMTS.Application.Features.Authorization;
using OMTS.Application.Features.Authorization.Models;
using OMTS.Application.Features.Sales.Returns;

namespace OMTS.Api.Controllers;

/// <summary>
/// CLAIM-03 (SCR-CLAIM-03): the Zone Manager of the invoice's zone and the Owner record a sales return of a whole invoice; only the Owner cancels one (CLAIM-03.3). The filter needs the right in
/// some zone; the service checks the zone of the invoice (BR-CLAIM-03, BR-FARM-08).
/// </summary>
[ApiController]
[Authorize]
public sealed class SalesReturnsController(SalesReturnService returns, SalesReturnCancellationService cancellations, SalesReturnViews views) : ControllerBase
{
    /// <summary>CLAIM-03 step 1: the invoice lines with their lots, the value, the debt after it; <c>customerId</c> is the profile chosen for a walk-in invoice.</summary>
    [HttpGet("api/sales-invoices/{id:guid}/sales-return/preview")]
    [RequirePermission(PermissionFunctions.SalesReturns, PermissionAction.Create, PermissionScope.AnyZone)]
    [ProducesResponseType<SalesReturnPreview>(StatusCodes.Status200OK)]
    public async Task<IActionResult> Preview(Guid id, [FromQuery] Guid? customerId, CancellationToken cancellationToken) =>
        (await views.PreviewAsync(id, customerId, cancellationToken)).ToActionResult(Ok);

    /// <summary>
    /// CLAIM-03: <c>reason</c>, every invoice line in <c>lines</c> with its <c>outcome</c> (<c>BackForSale</c> or <c>ThrownAway</c>), <c>rowVersion</c>. 409 MSG-CLAIM-04 (<c>ownerAgreed</c>) and
    /// MSG-HARVEST-02 (<c>thrownAwayLots</c>) are questions, answered by the field in <c>confirmationField</c>; a walk-in invoice needs <c>customerId</c> (MSG-GEN-01); 409 MSG-GEN-09 when cancelled or
    /// already returned; MSG06 when stale. 201 with the return, the debt after it and the money owed back.
    /// </summary>
    [HttpPost("api/sales-invoices/{id:guid}/sales-return")]
    [RequirePermission(PermissionFunctions.SalesReturns, PermissionAction.Create, PermissionScope.AnyZone)]
    [ProducesResponseType<SalesReturnSaved>(StatusCodes.Status201Created)]
    public async Task<IActionResult> Create(Guid id, CreateSalesReturnRequest request, CancellationToken cancellationToken) =>
        (await returns.CreateAsync(id, request, cancellationToken)).ToActionResult(saved => StatusCode(StatusCodes.Status201Created, saved));

    /// <summary>The return with its lines and, for the lots whose oysters went back for sale, their remaining kg and what it would be after a cancellation (CLAIM-03.3 step 2).</summary>
    [HttpGet("api/sales-returns/{id:guid}")]
    [RequirePermission(PermissionFunctions.SalesReturns, PermissionAction.View, PermissionScope.AnyZone)]
    [ProducesResponseType<SalesReturnView>(StatusCodes.Status200OK)]
    public async Task<IActionResult> Get(Guid id, CancellationToken cancellationToken) =>
        (await views.GetAsync(id, cancellationToken)).ToActionResult(Ok);

    /// <summary>CLAIM-03.3, Owner only: <c>reason</c>, the return's <c>rowVersion</c>. 409 MSG-CLAIM-06 until <c>confirmNegativeRemaining</c>; 409 MSG-GEN-09 when already cancelled; MSG06 when stale.</summary>
    [HttpPost("api/sales-returns/{id:guid}/cancel")]
    [RequireCooperativeOwner]
    [ProducesResponseType<SalesReturnView>(StatusCodes.Status200OK)]
    public async Task<IActionResult> Cancel(Guid id, CancelSalesReturnRequest request, CancellationToken cancellationToken) =>
        (await cancellations.CancelAsync(id, request, cancellationToken)).ToActionResult(Ok);
}
