using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using OMTS.Api.Common;
using OMTS.Api.Filters;
using OMTS.Application.Features.Authorization;
using OMTS.Application.Features.Authorization.Models;
using OMTS.Application.Features.Sales.Invoices;

namespace OMTS.Api.Controllers;

/// <summary>
/// SALE-05 (SCR-SALE-04): the Zone Manager and the Owner sell at the port; the Accountant only views (§I.4.4). The filter needs the right in some zone; the service checks the zone of the
/// invoice and of every lot (BR-SALE-06, BR-SALE-08). The products come from <c>GET api/products</c>, the lots from <c>GET api/harvest-lots/for-sale</c> and a new profile from
/// <c>POST api/customers</c> (SALE-05.1).
/// </summary>
[ApiController]
[Route("api/sales-invoices")]
[Authorize]
public sealed class SalesInvoicesController(SalesInvoiceService invoices, SalesInvoiceReadService reads) : ControllerBase
{
    /// <summary>
    /// 201 with the Completed invoice, the change and MSG-SALE-06; 409 MSG-SALE-10/11/12 asks a question, answered by the flag named in <c>confirmationField</c> in the same request;
    /// 409 MSG-GEN-09 names a lot no longer for sale or a request no longer open.
    /// </summary>
    [HttpPost]
    [RequirePermission(PermissionFunctions.SalesInvoices, PermissionAction.Create, PermissionScope.AnyZone)]
    [ProducesResponseType<SalesInvoiceSaved>(StatusCodes.Status201Created)]
    public async Task<IActionResult> Create(CreateSalesInvoiceRequest request, CancellationToken cancellationToken) =>
        (await invoices.CreateAsync(request, cancellationToken)).ToActionResult(saved => StatusCode(StatusCodes.Status201Created, saved));

    [HttpGet("{id:guid}")]
    [RequirePermission(PermissionFunctions.SalesInvoices, PermissionAction.View, PermissionScope.AnyZone)]
    [ProducesResponseType<SalesInvoiceView>(StatusCodes.Status200OK)]
    public async Task<IActionResult> Get(Guid id, CancellationToken cancellationToken) =>
        (await reads.GetAsync(id, cancellationToken)).ToActionResult(Ok);

    /// <summary>SALE-05.3: the requests of the profile an invoice can be linked to.</summary>
    [HttpGet("open-purchase-requests")]
    [RequirePermission(PermissionFunctions.SalesInvoices, PermissionAction.Create, PermissionScope.AnyZone)]
    [ProducesResponseType<IReadOnlyList<OpenPurchaseRequestView>>(StatusCodes.Status200OK)]
    public async Task<IActionResult> OpenPurchaseRequests(Guid customerId, CancellationToken cancellationToken) =>
        (await reads.OpenRequestsAsync(customerId, cancellationToken)).ToActionResult(Ok);
}
