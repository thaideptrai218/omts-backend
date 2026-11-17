using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using OMTS.Api.Common;
using OMTS.Api.Filters;
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
    private const string PhotosField = "Photos";

    // Three photos of 10 MB plus the multipart envelope.
    private const long MaxUploadBytes = 35 * 1024 * 1024;

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

    /// <summary>
    /// Records a session while the cycle is Open. Multipart (owner decision of 12/11/2026): <c>seedingDate</c>, <c>seedLotId</c>, one <c>laneNumbers</c> field per lane, <c>quantity</c>, optional <c>lanePercents</c>
    /// (a JSON object, lane number to percent; a lane left out takes 100) and 1 to 3 <c>photos</c> parts (JPEG or PNG, at most 10 MB each; 400 on <c>Photos</c> otherwise). Notifies nobody.
    /// </summary>
    [HttpPost("api/cultivation-cycles/{cycleId:guid}/seeding-records")]
    [RequestSizeLimit(MaxUploadBytes)]
    [UploadField(PhotosField)]
    [Consumes("multipart/form-data")]
    [ProducesResponseType<SeedingRecordSaved>(StatusCodes.Status201Created)]
    public async Task<IActionResult> Record(Guid cycleId, [FromForm] SeedingRecordForm form, CancellationToken cancellationToken) =>
        !Request.HasFormContentType
            ? StatusCode(StatusCodes.Status415UnsupportedMediaType)
            : (await records.RecordAsync(cycleId, form.ToRecord(Request.Form.Files.HasPartOtherThan(PhotosField)), await form.Photos.ToIncomingFilesAsync(cancellationToken), cancellationToken))
                .ToActionResult(r => StatusCode(StatusCodes.Status201Created, r));

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

    /// <summary>
    /// Corrects a session while the cycle is Open and no lane of it is in a harvest lot: the recording Zone Manager or the Cooperative Owner. Multipart: the fields of a session, <c>reason</c>, <c>rowVersion</c>,
    /// optional <c>lanePercents</c> (a lane left out keeps its share) and, to change the photos, <c>keepPhotoIds</c> (one field per photo to keep) and/or <c>photos</c>: a session with photos keeps 1 to 3. Notifies nobody.
    /// </summary>
    [HttpPut("api/seeding-records/{recordId:guid}")]
    [RequestSizeLimit(MaxUploadBytes)]
    [UploadField(PhotosField)]
    [Consumes("multipart/form-data")]
    [ProducesResponseType<SeedingRecordSaved>(StatusCodes.Status200OK)]
    public async Task<IActionResult> Correct(Guid recordId, [FromForm] SeedingRecordForm form, CancellationToken cancellationToken) =>
        !Request.HasFormContentType
            ? StatusCode(StatusCodes.Status415UnsupportedMediaType)
            : (await corrections.CorrectAsync(recordId, form.ToCorrect(Request.Form.Files.HasPartOtherThan(PhotosField)), await form.Photos.ToIncomingFilesAsync(cancellationToken), cancellationToken))
                .ToActionResult(Ok);

    /// <summary>Removes a session on the same conditions; ask with MSG-GEN-04 first. 204; the audit log keeps it with the reason.</summary>
    [HttpPost("api/seeding-records/{recordId:guid}/remove")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    public async Task<IActionResult> Remove(Guid recordId, RemoveSeedingRequest request, CancellationToken cancellationToken) =>
        (await corrections.RemoveAsync(recordId, request, cancellationToken)).ToActionResult(_ => NoContent());
}
