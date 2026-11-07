using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using OMTS.Api.Common;
using OMTS.Application.Features.Cultivation.Seeding;

namespace OMTS.Api.Controllers;

/// <summary>
/// CYCLE-02 – seeding sessions of a cycle, for a Zone Manager of the raft's zone and the Cooperative Owner, not for an Accountant (§I.4.4). The routes have no zone:
/// the services check the zone of the raft.
/// </summary>
[ApiController]
[Authorize]
public sealed class SeedingRecordsController(
    SeedingReadService reads, SeedingRecordService records, SeedingRecordCorrectionService corrections, SeedingCompletionService completions) : ControllerBase
{
    /// <summary>The seed lots to choose from, those bought for this raft first. With none: an empty list and MSG-CYCLE-03.</summary>
    [HttpGet("api/rafts/{raftId:guid}/seed-lots")]
    [ProducesResponseType<SeedLotList>(StatusCodes.Status200OK)]
    public async Task<IActionResult> SeedLots(Guid raftId, CancellationToken cancellationToken) =>
        (await reads.SeedLotsAsync(raftId, cancellationToken)).ToActionResult(Ok);

    /// <summary>One lane in a cycle: its seeding sessions with seed lot, receipt and supplier (CYCLE-05.0 step 3). Without a cycle: the Open cycle, else the one started last.</summary>
    [HttpGet("api/rafts/{raftId:guid}/lanes/{laneNumber:int}/progress")]
    [ProducesResponseType<LaneProgressView>(StatusCodes.Status200OK)]
    public async Task<IActionResult> LaneProgress(Guid raftId, int laneNumber, [FromQuery] Guid? cycleId, CancellationToken cancellationToken) =>
        (await reads.LaneProgressAsync(raftId, laneNumber, cycleId, cancellationToken)).ToActionResult(Ok);

    /// <summary>The Seeding screen: the lanes with their state, the sessions and the total per seed lot.</summary>
    [HttpGet("api/cultivation-cycles/{cycleId:guid}/seeding-records")]
    [ProducesResponseType<SeedingList>(StatusCodes.Status200OK)]
    public async Task<IActionResult> List(Guid cycleId, CancellationToken cancellationToken) =>
        (await reads.ListAsync(cycleId, cancellationToken)).ToActionResult(Ok);

    /// <summary>Records a session while the cycle is Open. Notifies nobody.</summary>
    [HttpPost("api/cultivation-cycles/{cycleId:guid}/seeding-records")]
    [ProducesResponseType<SeedingRecordSaved>(StatusCodes.Status201Created)]
    public async Task<IActionResult> Record(Guid cycleId, RecordSeedingRequest request, CancellationToken cancellationToken) =>
        (await records.RecordAsync(cycleId, request, cancellationToken)).ToActionResult(r => StatusCode(StatusCodes.Status201Created, r));

    /// <summary>The question before marking the seeding complete: MSG-CYCLE-01, with the lanes still Not seeded.</summary>
    [HttpGet("api/cultivation-cycles/{cycleId:guid}/seeding-complete/preview")]
    [ProducesResponseType<SeedingCompletionPreview>(StatusCodes.Status200OK)]
    public async Task<IActionResult> CompletionPreview(Guid cycleId, CancellationToken cancellationToken) =>
        (await completions.PreviewAsync(cycleId, cancellationToken)).ToActionResult(Ok);

    /// <summary>Marks the seeding complete, one time per cycle, and notifies the Cooperative Owner (MSG-CYCLE-02).</summary>
    [HttpPost("api/cultivation-cycles/{cycleId:guid}/seeding-complete")]
    [ProducesResponseType<SeedingCompleted>(StatusCodes.Status200OK)]
    public async Task<IActionResult> Complete(Guid cycleId, MarkSeedingCompleteRequest request, CancellationToken cancellationToken) =>
        (await completions.CompleteAsync(cycleId, request, cancellationToken)).ToActionResult(Ok);

    [HttpGet("api/seeding-records/{recordId:guid}")]
    [ProducesResponseType<SeedingRecordView>(StatusCodes.Status200OK)]
    public async Task<IActionResult> Get(Guid recordId, CancellationToken cancellationToken) =>
        (await reads.GetAsync(recordId, cancellationToken)).ToActionResult(Ok);

    /// <summary>Corrects a session while the cycle is Open and no lane of it is in a harvest lot: the recording Zone Manager or the Cooperative Owner. Notifies nobody.</summary>
    [HttpPut("api/seeding-records/{recordId:guid}")]
    [ProducesResponseType<SeedingRecordSaved>(StatusCodes.Status200OK)]
    public async Task<IActionResult> Correct(Guid recordId, CorrectSeedingRequest request, CancellationToken cancellationToken) =>
        (await corrections.CorrectAsync(recordId, request, cancellationToken)).ToActionResult(Ok);

    /// <summary>Removes a session on the same conditions; ask with MSG-GEN-04 first. 204; the audit log keeps it with the reason.</summary>
    [HttpPost("api/seeding-records/{recordId:guid}/remove")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    public async Task<IActionResult> Remove(Guid recordId, RemoveSeedingRequest request, CancellationToken cancellationToken) =>
        (await corrections.RemoveAsync(recordId, request, cancellationToken)).ToActionResult(_ => NoContent());
}
