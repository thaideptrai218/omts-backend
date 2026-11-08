using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using OMTS.Api.Common;
using OMTS.Application.Features.Traceability.Publishing;

namespace OMTS.Api.Controllers;

/// <summary>
/// TRACE-01.2 – publish the origin information of one harvest lot from the Trace lot screen (SCR-TRACE-01), for a Zone Manager of the lot's zone and the Cooperative Owner (PROVENANCE
/// Publish, §I.4.4), never the Accountant. The routes have no zone: the service checks the zone of the lot's raft. The end of a sale publishes through
/// <c>POST api/sales-invoices/{id}/finish</c> instead.
/// </summary>
[ApiController]
[Authorize]
public sealed class ProvenancePublishingController(LotPublishService publishing) : ControllerBase
{
    /// <summary>
    /// Step 2: whether Publish is offered (<c>canPublish</c>, else <c>block</c>), whether the lot has all its origin information (<c>ready</c>, else <c>missing</c> and MSG-TRACE-01),
    /// and the MSG-GEN-04 question to ask before the publish. Changes nothing.
    /// </summary>
    [HttpGet("api/harvest-lots/{lotId:guid}/publish-check")]
    [ProducesResponseType<LotPublishCheck>(StatusCodes.Status200OK)]
    public async Task<IActionResult> Check(Guid lotId, CancellationToken cancellationToken) =>
        (await publishing.CheckAsync(lotId, cancellationToken)).ToActionResult(Ok);

    /// <summary>
    /// Steps 3-4: publishes a Not published lot that is on a sales invoice, or, for the Cooperative Owner, publishes a Withdrawn lot again. 200 with the lot (Published) and MSG-GEN-05;
    /// 409 MSG-TRACE-01 with <c>lotCode</c> and <c>missing</c> when the lot misses origin information (nothing changes); 409 MSG-GEN-09 for a Published lot or one on no sales invoice;
    /// 403 MSG-GEN-08 for the Accountant, another zone, or a Zone Manager on a Withdrawn lot.
    /// </summary>
    [HttpPost("api/harvest-lots/{lotId:guid}/publish")]
    [ProducesResponseType<LotPublished>(StatusCodes.Status200OK)]
    public async Task<IActionResult> Publish(Guid lotId, CancellationToken cancellationToken) =>
        (await publishing.PublishAsync(lotId, cancellationToken)).ToActionResult(Ok);
}
