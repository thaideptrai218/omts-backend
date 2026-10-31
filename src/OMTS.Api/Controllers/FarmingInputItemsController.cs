using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using OMTS.Api.Common;
using OMTS.Api.Filters;
using OMTS.Application.Features.Purchasing.Items;
using OMTS.Domain.Enums;

namespace OMTS.Api.Controllers;

/// <summary>
/// PURCHASE-08 – farming input catalogue. Writes are Cooperative Owner only; reads are open to callers who view
/// purchase receipts, supplier payments or cultivation cycles.
/// </summary>
[ApiController]
[Route("api/farming-input-items")]
[Authorize]
public sealed class FarmingInputItemsController(FarmingInputItemService items) : ControllerBase
{
    [HttpGet]
    [ProducesResponseType<IReadOnlyList<FarmingInputItemView>>(StatusCodes.Status200OK)]
    public async Task<IActionResult> List(string? search, InputItemGroup? group, int? statusId, CancellationToken cancellationToken) =>
        (await items.ListAsync(search, group, statusId, cancellationToken)).ToActionResult(Ok);

    /// <summary>The proposed code of a new item (VT001, VT002…; GBR-03) and the unit every Seed item uses (BR-PURCHASE-10).</summary>
    [HttpGet("next-code")]
    [RequireCooperativeOwner]
    [ProducesResponseType<NewFarmingInputItemProposal>(StatusCodes.Status200OK)]
    public async Task<IActionResult> NextCode(CancellationToken cancellationToken) => Ok(await items.ProposeAsync(cancellationToken));

    [HttpGet("{id:guid}")]
    [ProducesResponseType<FarmingInputItemView>(StatusCodes.Status200OK)]
    public async Task<IActionResult> Get(Guid id, CancellationToken cancellationToken) =>
        (await items.GetAsync(id, cancellationToken)).ToActionResult(Ok);

    [HttpPost]
    [RequireCooperativeOwner]
    [ProducesResponseType<FarmingInputItemView>(StatusCodes.Status201Created)]
    public async Task<IActionResult> Create(SaveFarmingInputItemRequest request, CancellationToken cancellationToken) =>
        (await items.CreateAsync(request, cancellationToken)).ToActionResult(i => StatusCode(StatusCodes.Status201Created, i));

    [HttpPut("{id:guid}")]
    [RequireCooperativeOwner]
    [ProducesResponseType<FarmingInputItemView>(StatusCodes.Status200OK)]
    public async Task<IActionResult> Update(Guid id, SaveFarmingInputItemRequest request, CancellationToken cancellationToken) =>
        (await items.UpdateAsync(id, request, cancellationToken)).ToActionResult(Ok);

    [HttpPost("{id:guid}/discontinue")]
    [RequireCooperativeOwner]
    [ProducesResponseType<FarmingInputItemView>(StatusCodes.Status200OK)]
    public async Task<IActionResult> Discontinue(Guid id, CancellationToken cancellationToken) =>
        (await items.DiscontinueAsync(id, cancellationToken)).ToActionResult(Ok);

    /// <summary>Resume (PURCHASE-08.2). <c>/reactivate</c> is the older route of the same action.</summary>
    [HttpPost("{id:guid}/resume")]
    [HttpPost("{id:guid}/reactivate")]
    [RequireCooperativeOwner]
    [ProducesResponseType<FarmingInputItemView>(StatusCodes.Status200OK)]
    public async Task<IActionResult> Reactivate(Guid id, CancellationToken cancellationToken) =>
        (await items.ReactivateAsync(id, cancellationToken)).ToActionResult(Ok);
}
