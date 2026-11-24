using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using OMTS.Api.Common;
using OMTS.Api.Filters;
using OMTS.Application.Features.Authorization;
using OMTS.Application.Features.Authorization.Models;
using OMTS.Application.Features.Labor.Costs;

namespace OMTS.Api.Controllers;

/// <summary>
/// BR-LABOR-07 – the labour cost of each raft and of each cultivation cycle, from the attendance and job periods of the seasonal workers. Read only, for the Cooperative Owner and
/// the Accountant (PAYROLL View, decision of 19/11/2026): it is made of what workers are paid, so a Zone Manager gets 403 MSG-GEN-08.
/// Money is whole VND; dates are yyyy-MM-dd.
/// </summary>
[ApiController]
[Route("api/labor-costs")]
[Authorize]
[RequirePermission(PermissionFunctions.Payroll, PermissionAction.View, PermissionScope.NoZone)]
public sealed class LaborCostsController(LaborCostService costs, LaborCostExportService exports) : ControllerBase
{
    /// <summary>
    /// The cost of every raft in the period (<c>from</c> and <c>to</c> included, at most 366 days), with the cost of work at zone level as a row
    /// without a raft; <c>zoneId</c> keeps one zone. 400 for a period that is back to front or longer than a year.
    /// </summary>
    [HttpGet("rafts")]
    [ProducesResponseType<LaborCostByRaft>(StatusCodes.Status200OK)]
    public async Task<IActionResult> ByRaft([FromQuery] LaborCostQuery query, CancellationToken cancellationToken) =>
        (await costs.ByRaftAsync(query, cancellationToken)).ToActionResult(Ok);

    /// <summary>The cost by raft as a printable file ChiPhiNhanCong_{from}_{to}: the same selection as <c>rafts</c>; <c>format</c> xlsx (default) or pdf, another value is 400 naming "format". Needs PAYROLL Export.</summary>
    [HttpGet("export")]
    [RequirePermission(PermissionFunctions.Payroll, PermissionAction.Export, PermissionScope.NoZone)]
    public async Task<IActionResult> Export([FromQuery] LaborCostQuery query, [FromQuery] string? format, CancellationToken cancellationToken)
    {
        Response.Headers.CacheControl = "no-store";
        return (await exports.ExportAsync(query, format, cancellationToken)).ToActionResult(f => File(f.Content, f.ContentType, f.FileName));
    }

    /// <summary>Every cycle of a raft with its cost, hours, split by job type and by employee (BR-LABOR-07), newest first. PAYROLL View only: no cultivation journey right is needed.</summary>
    [HttpGet("rafts/{raftId:guid}/cycles")]
    [ProducesResponseType<IReadOnlyList<CycleLaborCost>>(StatusCodes.Status200OK)]
    public async Task<IActionResult> OfRaft(Guid raftId, CancellationToken cancellationToken) =>
        (await costs.OfRaftCyclesAsync(raftId, cancellationToken)).ToActionResult(Ok);

    /// <summary>The cost of one cycle: its raft on the days the cycle was Open (an Open cycle up to today), the close day included, day by day. 404 for an unknown cycle.</summary>
    [HttpGet("cycles/{cycleId:guid}")]
    [ProducesResponseType<CycleLaborCost>(StatusCodes.Status200OK)]
    public async Task<IActionResult> OfCycle(Guid cycleId, CancellationToken cancellationToken) =>
        (await costs.OfCycleAsync(cycleId, cancellationToken)).ToActionResult(Ok);
}
