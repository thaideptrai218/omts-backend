using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using OMTS.Api.Common;
using OMTS.Application.Features.Traceability.Tracing;

namespace OMTS.Api.Controllers;

/// <summary>
/// TRACE-02 – trace a harvest lot backward and forward (SCR-TRACE-01), for a Zone Manager of the lot's zone, the Cooperative Owner and the Accountant (view only, §I.4.4); traders are
/// refused. The routes have no zone: the service checks the zone of the lot's raft. Nothing is changed.
/// </summary>
[ApiController]
[Authorize]
public sealed class LotTracingController(LotTraceService traces) : ControllerBase
{
    /// <summary>
    /// Step 1: the lots of the caller's zones whose code starts with <c>lotCode</c> and/or that are on the sales invoice <c>invoiceCode</c>, the latest harvest first (at most 50).
    /// None: 200 with an empty list and MSG-GEN-10. Neither given: 400 MSG-GEN-01.
    /// </summary>
    [HttpGet("api/harvest-lots/trace-search")]
    [ProducesResponseType<LotTraceSearchResult>(StatusCodes.Status200OK)]
    public async Task<IActionResult> Search([FromQuery] LotTraceSearchQuery query, CancellationToken cancellationToken) =>
        (await traces.SearchAsync(query, cancellationToken)).ToActionResult(Ok);

    /// <summary>
    /// Steps 2-3: the lot with its status and publish state, the backward trace with what is missing (MSG-TRACE-02 in <c>message</c>, items in <c>missing</c>), the forward trace and
    /// the TRACE-03 correction marks. 403 MSG-GEN-08 outside the caller's zones, 404 for an unknown lot.
    /// </summary>
    [HttpGet("api/harvest-lots/{lotId:guid}/trace")]
    [ProducesResponseType<LotTrace>(StatusCodes.Status200OK)]
    public async Task<IActionResult> Trace(Guid lotId, CancellationToken cancellationToken) =>
        (await traces.TraceAsync(lotId, cancellationToken)).ToActionResult(Ok);

    /// <summary>TRACE-02.2: the trace as an .xlsx file <c>trace-{lotCode}.xlsx</c> (sheets Lot, Backward, Forward); Zone Manager of the zone and Owner, not the Accountant.</summary>
    [HttpGet("api/harvest-lots/{lotId:guid}/trace/export")]
    public async Task<IActionResult> Export(Guid lotId, CancellationToken cancellationToken) =>
        (await traces.ExportAsync(lotId, cancellationToken)).ToActionResult(f => File(f.Content, f.ContentType, f.FileName));
}
