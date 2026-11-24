using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using OMTS.Api.Common;
using OMTS.Api.Filters;
using OMTS.Application.Features.AccessManagement.Dtos;
using OMTS.Application.Features.Harvest;

namespace OMTS.Api.Controllers.Sales;

/// <summary>
/// The weighing sheets of the logged-in trader (owner decision of 24/11/2026): the lots harvested for the trader's orders, the trader's own cages weighed beside the cooperative, and the agreement
/// to the cooperative's completed weighing that the lot needs before it is invoiced. Only the trader's own lots: another lot answers 404.
/// </summary>
[ApiController]
[Authorize]
[RequireTrader]
[Route("api/me/weighings")]
public sealed class MyWeighingsController(TraderWeighingService weighings) : ControllerBase
{
    /// <summary>The latest harvest first, <c>pageSize</c> up to 100.</summary>
    [HttpGet]
    [ProducesResponseType<Paged<TraderWeighingRow>>(StatusCodes.Status200OK)]
    public async Task<IActionResult> List(int page = 1, int pageSize = 20, CancellationToken cancellationToken = default) =>
        Ok(await weighings.PageAsync(page, pageSize, cancellationToken));

    /// <summary>The cooperative's cages beside the trader's, and the agreement.</summary>
    [HttpGet("{lotId:guid}")]
    [ProducesResponseType<LotWeighingSheet>(StatusCodes.Status200OK)]
    public async Task<IActionResult> Get(Guid lotId, CancellationToken cancellationToken) =>
        (await weighings.SheetAsync(lotId, cancellationToken)).ToActionResult(Ok);

    /// <summary>One more cage as the trader weighed it; the same <c>clientOperationId</c> sent again weighs nothing. 400 MSG-GEN-01 on <c>Kg</c>; 409 MSG-GEN-09 once the lot is Closed.</summary>
    [HttpPost("{lotId:guid}/cages")]
    [ProducesResponseType<LotWeighingSheet>(StatusCodes.Status200OK)]
    public async Task<IActionResult> AddCage(Guid lotId, AddTraderCageRequest request, CancellationToken cancellationToken) =>
        (await weighings.AddCageAsync(lotId, request, cancellationToken)).ToActionResult(Ok);

    [HttpPut("{lotId:guid}/cages/{cageId:guid}")]
    [ProducesResponseType<LotWeighingSheet>(StatusCodes.Status200OK)]
    public async Task<IActionResult> UpdateCage(Guid lotId, Guid cageId, UpdateTraderCageRequest request, CancellationToken cancellationToken) =>
        (await weighings.UpdateCageAsync(lotId, cageId, request, cancellationToken)).ToActionResult(Ok);

    [HttpDelete("{lotId:guid}/cages/{cageId:guid}")]
    [ProducesResponseType<LotWeighingSheet>(StatusCodes.Status200OK)]
    public async Task<IActionResult> RemoveCage(Guid lotId, Guid cageId, CancellationToken cancellationToken) =>
        (await weighings.RemoveCageAsync(lotId, cageId, cancellationToken)).ToActionResult(Ok);

    /// <summary>
    /// Agrees to the cooperative's completed weighing, at its kg. 409 MSG-GEN-09 while it is not completed or a cage waits for completing again; MSG06 when it was completed again since
    /// <c>rowVersion</c> was read.
    /// </summary>
    [HttpPost("{lotId:guid}/agree")]
    [ProducesResponseType<LotWeighingSheet>(StatusCodes.Status200OK)]
    public async Task<IActionResult> Agree(Guid lotId, AgreeLotWeighingRequest request, CancellationToken cancellationToken) =>
        (await weighings.AgreeAsync(lotId, request, cancellationToken)).ToActionResult(Ok);
}
