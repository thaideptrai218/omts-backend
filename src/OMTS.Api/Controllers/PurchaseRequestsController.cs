using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using OMTS.Api.Common;
using OMTS.Api.Filters;
using OMTS.Application.Features.AccessManagement.Dtos;
using OMTS.Application.Features.Authorization;
using OMTS.Application.Features.Authorization.Models;
using OMTS.Application.Features.Sales.PurchaseRequests;

namespace OMTS.Api.Controllers;

/// <summary>
/// SALE-03 (SCR-SALE-02): the Owner responds; Zone Managers only view, every request, since requests have no zone (§I.4.4 R,
/// PM 03/11). The Accountant has no right here.
/// </summary>
[ApiController]
[Route("api/purchase-requests")]
[Authorize]
public sealed class PurchaseRequestsController(PurchaseRequestReadService reads, PurchaseRequestResponseService responses) : ControllerBase
{
    /// <summary>Those Awaiting response first, then newest first. <c>customerId</c> gives the requests of a profile (SALE-04.1).</summary>
    [HttpGet]
    [RequirePermission(PermissionFunctions.PurchaseRequests, PermissionAction.View, PermissionScope.AnyZone)]
    [ProducesResponseType<Paged<PurchaseRequestView>>(StatusCodes.Status200OK)]
    public async Task<IActionResult> Search(int? statusId, Guid? customerId, int page = 1, int pageSize = 20, CancellationToken cancellationToken = default) =>
        Ok(await reads.SearchAsync(statusId, customerId, page, pageSize, cancellationToken));

    [HttpGet("{id:guid}")]
    [RequirePermission(PermissionFunctions.PurchaseRequests, PermissionAction.View, PermissionScope.AnyZone)]
    [ProducesResponseType<PurchaseRequestView>(StatusCodes.Status200OK)]
    public async Task<IActionResult> Get(Guid id, CancellationToken cancellationToken) =>
        (await reads.GetAsync(id, cancellationToken)).ToActionResult(Ok);

    [HttpPost("{id:guid}/accept")]
    [RequireCooperativeOwner]
    [ProducesResponseType<PurchaseRequestView>(StatusCodes.Status200OK)]
    public async Task<IActionResult> Accept(Guid id, AcceptPurchaseRequest request, CancellationToken cancellationToken) =>
        (await responses.AcceptAsync(id, request, cancellationToken)).ToActionResult(Ok);

    [HttpPost("{id:guid}/decline")]
    [RequireCooperativeOwner]
    [ProducesResponseType<PurchaseRequestView>(StatusCodes.Status200OK)]
    public async Task<IActionResult> Decline(Guid id, DeclinePurchaseRequest request, CancellationToken cancellationToken) =>
        (await responses.DeclineAsync(id, request, cancellationToken)).ToActionResult(Ok);
}
