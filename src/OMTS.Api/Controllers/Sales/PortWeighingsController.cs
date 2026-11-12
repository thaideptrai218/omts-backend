using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using OMTS.Api.Common;
using OMTS.Api.Filters;
using OMTS.Application.Features.Authorization;
using OMTS.Application.Features.Authorization.Models;
using OMTS.Application.Features.Sales.Weighings;
using OMTS.Application.Features.Sales.Weighings.Dtos;

namespace OMTS.Api.Controllers.Sales;

/// <summary>The weighing at the port, staff side: the Zone Manager and the Owner record and correct it, whoever views the invoices reads it. The service checks the zone of every lot.</summary>
[ApiController]
[Authorize]
[Route("api/port-weighings")]
public sealed class PortWeighingsController(PortWeighingService weighings) : ControllerBase
{
    [HttpGet]
    [RequirePermission(PermissionFunctions.SalesInvoices, PermissionAction.View, PermissionScope.AnyZone)]
    [ProducesResponseType<IReadOnlyList<PortWeighingView>>(StatusCodes.Status200OK)]
    public async Task<IActionResult> List(Guid purchaseRequestId, CancellationToken cancellationToken) =>
        (await weighings.ListOfRequestAsync(purchaseRequestId, cancellationToken)).ToActionResult(Ok);

    [HttpGet("{id:guid}")]
    [RequirePermission(PermissionFunctions.SalesInvoices, PermissionAction.View, PermissionScope.AnyZone)]
    [ProducesResponseType<PortWeighingView>(StatusCodes.Status200OK)]
    public async Task<IActionResult> Get(Guid id, CancellationToken cancellationToken) =>
        (await weighings.GetAsync(id, cancellationToken)).ToActionResult(Ok);

    /// <summary>201 with the weighing; 409 MSG-GEN-09 for a request not Accepted or Fulfilled, a lot no longer for sale, or while another weighing of the request waits for the trader.</summary>
    [HttpPost]
    [RequirePermission(PermissionFunctions.SalesInvoices, PermissionAction.Create, PermissionScope.AnyZone)]
    [ProducesResponseType<PortWeighingView>(StatusCodes.Status201Created)]
    public async Task<IActionResult> Create(CreatePortWeighingRequest request, CancellationToken cancellationToken) =>
        (await weighings.CreateAsync(request, cancellationToken)).ToActionResult(saved => StatusCode(StatusCodes.Status201Created, saved));

    /// <summary>A correction until the trader confirms: 409 MSG-GEN-09 after that.</summary>
    [HttpPut("{id:guid}")]
    [RequirePermission(PermissionFunctions.SalesInvoices, PermissionAction.Create, PermissionScope.AnyZone)]
    [ProducesResponseType<PortWeighingView>(StatusCodes.Status200OK)]
    public async Task<IActionResult> Update(Guid id, UpdatePortWeighingRequest request, CancellationToken cancellationToken) =>
        (await weighings.UpdateAsync(id, request, cancellationToken)).ToActionResult(Ok);
}
