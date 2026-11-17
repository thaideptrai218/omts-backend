using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using OMTS.Api.Common;
using OMTS.Api.Filters;
using OMTS.Application.Common.Interfaces;
using OMTS.Application.Features.Sales.PurchaseRequests;

namespace OMTS.Api.Controllers;

/// <summary>
/// SALE-01, SALE-02 and SALE-10 (requests): My purchases of the logged-in trader (SCR-SALE-01). Only the trader's own data: a
/// request of another trader answers 404.
/// </summary>
[ApiController]
[Route("api/me")]
[Authorize]
[RequireTrader]
public sealed class MePurchasesController(
    TraderPurchaseRequestService requests,
    PurchaseRequestReadService reads,
    ICurrentUser currentUser) : ControllerBase
{
    private Guid AccountId => currentUser.AccountId!.Value;

    [HttpGet("purchases")]
    [ProducesResponseType<MyPurchases>(StatusCodes.Status200OK)]
    public async Task<IActionResult> List(int page = 1, int pageSize = 20, CancellationToken cancellationToken = default) =>
        Ok(await reads.MyPurchasesAsync(AccountId, page, pageSize, cancellationToken));

    /// <summary>The request a response notification opens (SALE-10.2).</summary>
    [HttpGet("purchase-requests/{id:guid}")]
    [ProducesResponseType<TraderPurchaseRequestView>(StatusCodes.Status200OK)]
    public async Task<IActionResult> Get(Guid id, CancellationToken cancellationToken) =>
        (await reads.GetMineAsync(AccountId, id, cancellationToken)).ToActionResult(Ok);

    /// <summary>201 for a new request; 200 when a retry with the same <c>clientOperationId</c> answers the request already saved.</summary>
    [HttpPost("purchase-requests")]
    [ProducesResponseType<PurchaseRequestSaved>(StatusCodes.Status201Created)]
    [ProducesResponseType<PurchaseRequestSaved>(StatusCodes.Status200OK)]
    public async Task<IActionResult> Place(PlacePurchaseRequest request, CancellationToken cancellationToken) =>
        (await requests.PlaceAsync(AccountId, request, cancellationToken)).ToActionResult(r => r.Replayed ? Ok(r) : StatusCode(StatusCodes.Status201Created, r));

    [HttpPost("purchase-requests/{id:guid}/cancel")]
    [ProducesResponseType<TraderPurchaseRequestView>(StatusCodes.Status200OK)]
    public async Task<IActionResult> Cancel(Guid id, CancelPurchaseRequest request, CancellationToken cancellationToken) =>
        (await requests.CancelAsync(AccountId, id, request, cancellationToken)).ToActionResult(Ok);
}
