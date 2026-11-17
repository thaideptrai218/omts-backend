using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using OMTS.Api.Common;
using OMTS.Api.Filters;
using OMTS.Application.Features.Sales.Weighings;
using OMTS.Application.Features.Sales.Weighings.Dtos;

namespace OMTS.Api.Controllers.Sales;

/// <summary>The weighings of the logged-in trader, and their side of closing the figures. A weighing of another profile answers 404.</summary>
[ApiController]
[Authorize]
[RequireTrader]
[Route("api/me/port-weighings")]
public sealed class MyPortWeighingsController(MyPortWeighingService weighings) : ControllerBase
{
    /// <summary>Newest first, <c>pageSize</c> up to 100; MSG-GEN-11 in <c>message</c> when there is none yet.</summary>
    [HttpGet]
    [ProducesResponseType<MyPortWeighingList>(StatusCodes.Status200OK)]
    public async Task<IActionResult> List(int page = 1, int pageSize = 20, CancellationToken cancellationToken = default) =>
        (await weighings.ListAsync(page, pageSize, cancellationToken)).ToActionResult(Ok);

    [HttpGet("{id:guid}")]
    [ProducesResponseType<PortWeighingView>(StatusCodes.Status200OK)]
    public async Task<IActionResult> Get(Guid id, CancellationToken cancellationToken) =>
        (await weighings.GetAsync(id, cancellationToken)).ToActionResult(Ok);

    /// <summary>200 with the confirmed weighing; 409 when it was corrected since the trader read it.</summary>
    [HttpPost("{id:guid}/confirm")]
    [ProducesResponseType<PortWeighingView>(StatusCodes.Status200OK)]
    public async Task<IActionResult> Confirm(Guid id, ConfirmPortWeighingRequest request, CancellationToken cancellationToken) =>
        (await weighings.ConfirmAsync(id, request, cancellationToken)).ToActionResult(Ok);
}
