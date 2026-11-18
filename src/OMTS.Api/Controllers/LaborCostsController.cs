using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using OMTS.Api.Common;
using OMTS.Api.Filters;
using OMTS.Application.Features.Labor.Costs;

namespace OMTS.Api.Controllers;

/// <summary>
/// BR-LABOR-07 – the labour cost of each raft and of each cultivation cycle, from the attendance and job periods of the seasonal workers. The
/// Cooperative Owner only, by a fixed rule on the account (<see cref="RequireCooperativeOwnerAttribute"/>): it is made of what workers are paid.
/// Money is whole VND; dates are yyyy-MM-dd.
/// </summary>
[ApiController]
[Route("api/labor-costs")]
[Authorize]
[RequireCooperativeOwner]
public sealed class LaborCostsController(LaborCostService costs) : ControllerBase
{
    /// <summary>
    /// The cost of every raft in the period (<c>from</c> and <c>to</c> included, at most 366 days), with the cost of work at zone level as a row
    /// without a raft; <c>zoneId</c> keeps one zone. 400 for a period that is back to front or longer than a year.
    /// </summary>
    [HttpGet("rafts")]
    [ProducesResponseType<LaborCostByRaft>(StatusCodes.Status200OK)]
    public async Task<IActionResult> ByRaft([FromQuery] LaborCostQuery query, CancellationToken cancellationToken) =>
        (await costs.ByRaftAsync(query, cancellationToken)).ToActionResult(Ok);

    /// <summary>The cost of one cycle: its raft on the days the cycle was Open (an Open cycle up to today), the close day included, day by day. 404 for an unknown cycle.</summary>
    [HttpGet("cycles/{cycleId:guid}")]
    [ProducesResponseType<CycleLaborCost>(StatusCodes.Status200OK)]
    public async Task<IActionResult> OfCycle(Guid cycleId, CancellationToken cancellationToken) =>
        (await costs.OfCycleAsync(cycleId, cancellationToken)).ToActionResult(Ok);
}
