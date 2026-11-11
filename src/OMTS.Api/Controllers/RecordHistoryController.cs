using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using OMTS.Api.Common;
using OMTS.Application.Features.Audit;
using OMTS.Application.Features.Audit.Dtos;

namespace OMTS.Api.Controllers;

/// <summary>
/// The history of a farming record (decision of 12/11/2026): who, when, which fields changed from which value to which, the reason, from the audit snapshots. Each route is read with the permission
/// of the record's own read in the zone of its events; a removed record keeps its history; a record with no event answers 404.
/// </summary>
[ApiController]
[Authorize]
public sealed class RecordHistoryController(RecordHistoryService history) : ControllerBase
{
    [HttpGet("api/harvest-lots/{lotId:guid}/history")]
    [ProducesResponseType<RecordHistory>(StatusCodes.Status200OK)]
    public async Task<IActionResult> HarvestLot(Guid lotId, CancellationToken cancellationToken) =>
        (await history.ReadAsync(RecordHistoryService.HarvestLot, lotId, cancellationToken)).ToActionResult(Ok);

    [HttpGet("api/seeding-records/{recordId:guid}/history")]
    [ProducesResponseType<RecordHistory>(StatusCodes.Status200OK)]
    public async Task<IActionResult> SeedingRecord(Guid recordId, CancellationToken cancellationToken) =>
        (await history.ReadAsync(RecordHistoryService.SeedingRecord, recordId, cancellationToken)).ToActionResult(Ok);

    [HttpGet("api/material-usages/{usageId:guid}/history")]
    [ProducesResponseType<RecordHistory>(StatusCodes.Status200OK)]
    public async Task<IActionResult> MaterialUsage(Guid usageId, CancellationToken cancellationToken) =>
        (await history.ReadAsync(RecordHistoryService.MaterialUsage, usageId, cancellationToken)).ToActionResult(Ok);

    [HttpGet("api/cultivation-logs/{logId:guid}/history")]
    [ProducesResponseType<RecordHistory>(StatusCodes.Status200OK)]
    public async Task<IActionResult> CultivationLog(Guid logId, CancellationToken cancellationToken) =>
        (await history.ReadAsync(RecordHistoryService.CultivationLog, logId, cancellationToken)).ToActionResult(Ok);

    [HttpGet("api/cultivation-cycles/{cycleId:guid}/history")]
    [ProducesResponseType<RecordHistory>(StatusCodes.Status200OK)]
    public async Task<IActionResult> CultivationCycle(Guid cycleId, CancellationToken cancellationToken) =>
        (await history.ReadAsync(RecordHistoryService.CultivationCycle, cycleId, cancellationToken)).ToActionResult(Ok);
}
