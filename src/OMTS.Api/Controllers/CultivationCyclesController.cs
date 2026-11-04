using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using OMTS.Api.Common;
using OMTS.Api.Filters;
using OMTS.Application.Features.Authorization;
using OMTS.Application.Features.Authorization.Models;
using OMTS.Application.Features.Cultivation.Cycles;
using OMTS.Application.Features.Cultivation.Progress;

namespace OMTS.Api.Controllers;

/// <summary>
/// CYCLE-01 and CYCLE-05 – cultivation cycles of a raft and the lane states of a date, for a Zone Manager of the raft's zone and
/// the Cooperative Owner, not for an Accountant (§I.4.4). Routes without a zone check the zone of the raft in the service.
/// </summary>
[ApiController]
[Authorize]
public sealed class CultivationCyclesController(
    CultivationCycleReadService reads, CultivationCycleService cycles, CultivationProgressService progress) : ControllerBase
{
    /// <summary>Every raft of the zone with its Open or last closed cycle and the commands allowed (SCR-CYCLE-01).</summary>
    [HttpGet("api/zones/{zoneId:guid}/cultivation-cycles")]
    [RequirePermission(PermissionFunctions.CultivationCycles, PermissionAction.View)]
    [ProducesResponseType<IReadOnlyList<ZoneCycleItem>>(StatusCodes.Status200OK)]
    public async Task<IActionResult> ListOfZone(Guid zoneId, CancellationToken cancellationToken) =>
        (await reads.ListOfZoneAsync(zoneId, cancellationToken)).ToActionResult(Ok);

    /// <summary>Starts a cycle; without a start date it starts today. MSG-GEN-09 for a Discontinued raft or an Open cycle.</summary>
    [HttpPost("api/rafts/{raftId:guid}/cultivation-cycles")]
    [ProducesResponseType<CycleStarted>(StatusCodes.Status201Created)]
    public async Task<IActionResult> Start(Guid raftId, StartCycleRequest request, CancellationToken cancellationToken) =>
        (await cycles.StartAsync(raftId, request, cancellationToken)).ToActionResult(r => StatusCode(StatusCodes.Status201Created, r));

    /// <summary>The cycles of the raft with their start and close dates, the newest first (CYCLE-05.1).</summary>
    [HttpGet("api/rafts/{raftId:guid}/cultivation-cycles")]
    [ProducesResponseType<IReadOnlyList<CycleListItem>>(StatusCodes.Status200OK)]
    public async Task<IActionResult> ListOfRaft(Guid raftId, CancellationToken cancellationToken) =>
        (await reads.ListOfRaftAsync(raftId, cancellationToken)).ToActionResult(Ok);

    /// <summary>The lanes of the raft on a date (today by default) or at the close of a cycle, with the cycle open then (CYCLE-05).</summary>
    [HttpGet("api/rafts/{raftId:guid}/progress")]
    [ProducesResponseType<CultivationProgressView>(StatusCodes.Status200OK)]
    public async Task<IActionResult> Progress(Guid raftId, DateOnly? date, Guid? cycleId, CancellationToken cancellationToken) =>
        (await progress.ViewAsync(raftId, date, cycleId, cancellationToken)).ToActionResult(Ok);

    [HttpGet("api/cultivation-cycles/{cycleId:guid}")]
    [ProducesResponseType<CycleView>(StatusCodes.Status200OK)]
    public async Task<IActionResult> Get(Guid cycleId, CancellationToken cancellationToken) =>
        (await reads.GetAsync(cycleId, cancellationToken)).ToActionResult(Ok);

    /// <summary>Edits the start date and the plan estimates of an Open cycle (CYCLE-01.1).</summary>
    [HttpPut("api/cultivation-cycles/{cycleId:guid}")]
    [ProducesResponseType<CycleSaved>(StatusCodes.Status200OK)]
    public async Task<IActionResult> Edit(Guid cycleId, EditCycleRequest request, CancellationToken cancellationToken) =>
        (await cycles.EditAsync(cycleId, request, cancellationToken)).ToActionResult(Ok);

    /// <summary>Removes an Open cycle with no record, started by mistake (CYCLE-01.2). 204; the audit log keeps it.</summary>
    [HttpPost("api/cultivation-cycles/{cycleId:guid}/cancel")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    public async Task<IActionResult> Cancel(Guid cycleId, CancelCycleRequest request, CancellationToken cancellationToken) =>
        (await cycles.CancelAsync(cycleId, request, cancellationToken)).ToActionResult(_ => NoContent());
}
