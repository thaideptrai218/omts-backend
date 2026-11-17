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
public sealed class SalesReturnsController(
    SalesReturnService returns, SalesReturnCancellationService cancellations, SalesReturnViews views, SalesReturnListService list, SalesReturnListFileService listFiles) : ControllerBase
{
    /// <summary>
    /// The returns of the zones the caller can view, cancelled ones included. Query: <c>from</c>, <c>to</c> (return date, yyyy-MM-dd), <c>customerId</c>, <c>zoneId</c>, <c>statusId</c>, <c>search</c> (code of the
    /// return, the invoice or the customer, or the name of the customer), <c>page</c>, <c>pageSize</c> (1 to 100), <c>sort</c> (<c>code</c>, <c>returnDate</c>, <c>value</c>) and <c>sortDir</c> (<c>asc</c>, <c>desc</c>,
    /// the default). The totals count the returns that stand among every match. 400 MSG-GEN-01 for an invalid sort or period; 403 MSG-GEN-08 for a zone the caller cannot view.
    /// </summary>
    [HttpGet("api/sales-returns")]
    [RequirePermission(PermissionFunctions.SalesReturns, PermissionAction.View, PermissionScope.AnyZone)]
    [ProducesResponseType<SalesReturnListPage>(StatusCodes.Status200OK)]
    public async Task<IActionResult> Search([FromQuery] SalesReturnListQuery query, CancellationToken cancellationToken) =>
        (await list.SearchAsync(query, cancellationToken)).ToActionResult(Ok);

    /// <summary>The list as filtered and sorted, as .xlsx (at most <see cref="SalesReturnListFileService.MaxRows"/> rows), with the zone rule of the list.</summary>
    [HttpGet("api/sales-returns/export")]
    [RequirePermission(PermissionFunctions.SalesReturns, PermissionAction.View, PermissionScope.AnyZone)]
    public async Task<IActionResult> Export([FromQuery] SalesReturnListQuery query, CancellationToken cancellationToken) =>
        (await listFiles.ExportAsync(query, cancellationToken)).ToActionResult(f => File(f.Content, f.ContentType, f.FileName));

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
