using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using OMTS.Api.Common;
using OMTS.Api.Filters;
using OMTS.Application.Features.Authorization;
using OMTS.Application.Features.Authorization.Models;
using OMTS.Application.Features.Cultivation.Closing;
using OMTS.Application.Features.Cultivation.Cycles;
using OMTS.Application.Features.Cultivation.Progress;

namespace OMTS.Api.Controllers;

/// <summary>
/// CYCLE-01, CYCLE-05 and CYCLE-06 – cultivation cycles of a raft and the lane states of a date, for a Zone Manager of the raft's zone and
/// the Cooperative Owner, not for an Accountant (§I.4.4). Routes without a zone check the zone of the raft in the service.
/// </summary>
[ApiController]
[Authorize]
public sealed class CultivationCyclesController(
    CultivationCycleReadService reads, CultivationCycleService cycles, CultivationProgressService progress, CycleCloseService closes) : ControllerBase
{
    private const string PhotosField = "Photos";

    // Three photos of 10 MB plus the multipart envelope.
    private const long MaxUploadBytes = 35 * 1024 * 1024;

    /// <summary>Every raft of the zone with its Open or last closed cycle and the commands allowed (SCR-CYCLE-01).</summary>
    [HttpGet("api/zones/{zoneId:guid}/cultivation-cycles")]
    [RequirePermission(PermissionFunctions.CultivationCycles, PermissionAction.View)]
    [ProducesResponseType<IReadOnlyList<ZoneCycleItem>>(StatusCodes.Status200OK)]
    public async Task<IActionResult> ListOfZone(Guid zoneId, CancellationToken cancellationToken) =>
        (await reads.ListOfZoneAsync(zoneId, cancellationToken)).ToActionResult(Ok);

    /// <summary>
    /// Starts a cycle by hand from a nursery batch that is ready (v2, 13/11/2026); without a start date it starts today. Multipart: <c>raftId</c>, <c>nurseryBatchId</c> (required: Completed, same zone as the raft, pieces left in its pool),
    /// <c>targetYieldKg</c> (required, above 0), <c>startDate</c>, <c>note</c> and 1 to 3 <c>photos</c> parts of the plan (JPEG or PNG, at most 10 MB each; 400 on <c>Photos</c> otherwise). MSG-GEN-09 for a Discontinued raft
    /// or a batch that does not qualify (<c>reasonCode</c>), MSG-GEN-06 for an Open cycle. There is no other way to start a cycle: the old free seed lot path is closed.
    /// </summary>
    [HttpPost("api/cultivation-cycles")]
    [RequestSizeLimit(MaxUploadBytes)]
    [UploadField(PhotosField)]
    [Consumes("multipart/form-data")]
    [ProducesResponseType<CycleStarted>(StatusCodes.Status201Created)]
    public async Task<IActionResult> Create([FromForm] CyclePlanForm form, CancellationToken cancellationToken) =>
        !Request.HasFormContentType
            ? StatusCode(StatusCodes.Status415UnsupportedMediaType)
            : (await cycles.CreateAsync(form.ToCreate(Request.Form.Files.HasPartOtherThan(PhotosField)), await form.Photos.ToIncomingFilesAsync(cancellationToken), cancellationToken))
                .ToActionResult(r => StatusCode(StatusCodes.Status201Created, r));

    /// <summary>The same start with the raft in the route (the quick button of the raft detail): <c>raftId</c> of the form is ignored.</summary>
    [HttpPost("api/rafts/{raftId:guid}/cultivation-cycles")]
    [RequestSizeLimit(MaxUploadBytes)]
    [UploadField(PhotosField)]
    [Consumes("multipart/form-data")]
    [ProducesResponseType<CycleStarted>(StatusCodes.Status201Created)]
    public async Task<IActionResult> CreateOnRaft(Guid raftId, [FromForm] CyclePlanForm form, CancellationToken cancellationToken) =>
        !Request.HasFormContentType
            ? StatusCode(StatusCodes.Status415UnsupportedMediaType)
            : (await cycles.CreateAsync(form.ToCreate(Request.Form.Files.HasPartOtherThan(PhotosField), raftId), await form.Photos.ToIncomingFilesAsync(cancellationToken), cancellationToken))
                .ToActionResult(r => StatusCode(StatusCodes.Status201Created, r));
    /// <summary>
    /// Changes the plan photos of a cycle: <c>rowVersion</c>, one <c>keepPhotoIds</c> field per photo to keep and the new <c>photos</c>; 1 to 3 remain.
    /// </summary>
    [HttpPut("api/cultivation-cycles/{cycleId:guid}/plan-photos")]
    [RequestSizeLimit(MaxUploadBytes)]
    [UploadField(PhotosField)]
    [Consumes("multipart/form-data")]
    [ProducesResponseType<CycleSaved>(StatusCodes.Status200OK)]
    public async Task<IActionResult> ChangePlanPhotos(Guid cycleId, [FromForm] CyclePlanForm form, CancellationToken cancellationToken) =>
        !Request.HasFormContentType
            ? StatusCode(StatusCodes.Status415UnsupportedMediaType)
            : (await cycles.ChangePlanPhotosAsync(cycleId, form.ToPhotos(Request.Form.Files.HasPartOtherThan(PhotosField)), await form.Photos.ToIncomingFilesAsync(cancellationToken), cancellationToken))
                .ToActionResult(Ok);

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

    /// <summary>Edits an Open cycle (CYCLE-01.1): the target yield, the start date and the note any time; the nursery batch only before the first seeding record (409 CYCLE_SEED_LOCKED).</summary>
    [HttpPut("api/cultivation-cycles/{cycleId:guid}")]
    [ProducesResponseType<CycleSaved>(StatusCodes.Status200OK)]
    public async Task<IActionResult> Edit(Guid cycleId, EditCycleRequest request, CancellationToken cancellationToken) =>
        (await cycles.EditAsync(cycleId, request, cancellationToken)).ToActionResult(Ok);

    /// <summary>Removes an Open cycle with no record, started by mistake (CYCLE-01.2). 204; the audit log keeps it.</summary>
    [HttpPost("api/cultivation-cycles/{cycleId:guid}/cancel")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    public async Task<IActionResult> Cancel(Guid cycleId, CancelCycleRequest request, CancellationToken cancellationToken) =>
        (await cycles.CancelAsync(cycleId, request, cancellationToken)).ToActionResult(_ => NoContent());

    /// <summary>The cycle summary (BR-CYCLE-06): of a Closed cycle on its close date with the assessment, of an Open one as if it closed today (CYCLE-05.1).</summary>
    [HttpGet("api/cultivation-cycles/{cycleId:guid}/summary")]
    [ProducesResponseType<CycleSummary>(StatusCodes.Status200OK)]
    public async Task<IActionResult> Summary(Guid cycleId, CancellationToken cancellationToken) =>
        (await reads.SummaryAsync(cycleId, cancellationToken)).ToActionResult(Ok);

    /// <summary>What to show before closing (CYCLE-06 steps 1, 2 and 4): the summary, whether the close is early (MSG-CYCLE-07) and the confirmation (MSG-CYCLE-05).</summary>
    [HttpGet("api/cultivation-cycles/{cycleId:guid}/close/preview")]
    [ProducesResponseType<CycleClosePreview>(StatusCodes.Status200OK)]
    public async Task<IActionResult> ClosePreview(Guid cycleId, CancellationToken cancellationToken) =>
        (await closes.PreviewAsync(cycleId, cancellationToken)).ToActionResult(Ok);

    /// <summary>
    /// Closes an Open cycle today with the assessment (CYCLE-06) and notifies the Cooperative Owner (MSG-CYCLE-06). An early close by a Zone Manager needs
    /// <c>ownerApproved: true</c>, else 409 MSG-CYCLE-07 and nothing changes. A Closed cycle answers MSG-GEN-09 and is never reopened.
    /// </summary>
    [HttpPost("api/cultivation-cycles/{cycleId:guid}/close")]
    [ProducesResponseType<CycleClosed>(StatusCodes.Status200OK)]
    public async Task<IActionResult> Close(Guid cycleId, CloseCycleRequest request, CancellationToken cancellationToken) =>
        (await closes.CloseAsync(cycleId, request, cancellationToken)).ToActionResult(Ok);
}
