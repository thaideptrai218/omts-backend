using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using OMTS.Api.Common;
using OMTS.Api.Filters;
using OMTS.Application.Features.AccessManagement;
using OMTS.Application.Features.AccessManagement.Dtos;

namespace OMTS.Api.Controllers;

/// <summary>Trader accounts requested from the Zalo Mini App; the Cooperative Owner approves or refuses them (web and Mini App).</summary>
[ApiController]
[Route("api/trader-requests")]
[Authorize]
[RequireCooperativeOwner]
public sealed class TraderRequestsController(TraderRequestService requests) : ControllerBase
{
    /// <summary>Requests waiting for a decision, oldest first; <c>rejected=true</c> lists the refused ones.</summary>
    [HttpGet]
    [ProducesResponseType<IReadOnlyList<TraderRequestView>>(StatusCodes.Status200OK)]
    public async Task<IActionResult> List(bool rejected = false, CancellationToken cancellationToken = default) =>
        Ok(await requests.ListAsync(rejected, cancellationToken));

    /// <summary>Opens the trader account (customer profile linked) and notifies the trader.</summary>
    [HttpPost("{accountId:guid}/approve")]
    [ProducesResponseType<TraderRequestView>(StatusCodes.Status200OK)]
    public async Task<IActionResult> Approve(Guid accountId, CancellationToken cancellationToken) =>
        (await requests.ApproveAsync(accountId, cancellationToken)).ToActionResult(Ok);

    /// <summary>Refuses the request; the reason (required) is shown to the trader in the Mini App.</summary>
    [HttpPost("{accountId:guid}/reject")]
    [ProducesResponseType<TraderRequestView>(StatusCodes.Status200OK)]
    public async Task<IActionResult> Reject(Guid accountId, ReasonRequest request, CancellationToken cancellationToken) =>
        (await requests.RejectAsync(accountId, request, cancellationToken)).ToActionResult(Ok);
}
