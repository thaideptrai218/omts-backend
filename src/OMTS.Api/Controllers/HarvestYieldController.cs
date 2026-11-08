using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using OMTS.Api.Common;
using OMTS.Api.Filters;
using OMTS.Application.Features.Authorization;
using OMTS.Application.Features.Authorization.Models;
using OMTS.Application.Features.Harvest;

namespace OMTS.Api.Controllers;

/// <summary>
/// HARVEST-04 – farming yield (SCR-HARVEST-03): the Owner for any zone or all zones, a Zone Manager in the zones of their pairs, not an Accountant (§I.4.4). A pair in any zone opens
/// the route; the service limits the zones and answers MSG-GEN-08 for a zone outside them.
/// </summary>
[ApiController]
[Route("api/harvest-yield")]
[Authorize]
public sealed class HarvestYieldController(HarvestYieldService yield) : ControllerBase
{
    /// <summary>
    /// Groups of lots harvested in the period (<c>from</c>/<c>to</c>, default the current month) of the zones (<c>zoneIds</c>, repeated; none: all the caller can access), by
    /// <c>groupBy</c> raft (default), zone or cycle; by cycle, every lot of each cycle that has a lot in the period. MSG-HARVEST-04 in <c>notFinalNotice</c> when a lot is still
    /// for sale, MSG-GEN-10 in <c>message</c> when there is no lot; 400 MSG-GEN-01 for a start after the end or an unknown grouping.
    /// </summary>
    [HttpGet]
    [RequirePermission(PermissionFunctions.HarvestLots, PermissionAction.View, PermissionScope.AnyZone)]
    [ProducesResponseType<HarvestYieldReport>(StatusCodes.Status200OK)]
    public async Task<IActionResult> Report([FromQuery] HarvestYieldQuery query, CancellationToken cancellationToken) =>
        (await yield.ReportAsync(query, cancellationToken)).ToActionResult(Ok);

    /// <summary>HARVEST-04.2: the same report as yield-{groupBy}-{from}-{to}.xlsx, sheets Groups (with a total row) and Lots, with the Not final marks.</summary>
    [HttpGet("export")]
    [RequirePermission(PermissionFunctions.HarvestLots, PermissionAction.Export, PermissionScope.AnyZone)]
    public async Task<IActionResult> Export([FromQuery] HarvestYieldQuery query, CancellationToken cancellationToken) =>
        (await yield.ExportAsync(query, cancellationToken)).ToActionResult(f => File(f.Content, f.ContentType, f.FileName));
}
