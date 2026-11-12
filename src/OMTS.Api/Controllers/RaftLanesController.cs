using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using OMTS.Api.Common;
using OMTS.Api.Filters;
using OMTS.Application.Common.Results;
using OMTS.Application.Features.Farming.Rafts;

namespace OMTS.Api.Controllers;

/// <summary>
/// Lane incidents of a raft (decision of 12/11/2026): report a lane damaged, record its repair from the stock, put it out of service, and read its history. For the Cooperative Owner and a Zone Manager
/// of the raft's zone (the services check the zone of the raft); a lane keeps its number for good, so there is no replace, no removal from the middle and no renumbering.
/// </summary>
[ApiController]
[Authorize]
public sealed class RaftLanesController(LaneIncidentService commands, LaneIncidentReadService reads, LaneIncidentPhotoService photos) : ControllerBase
{
    /// <summary>
    /// Reports the lane damaged: seeding and harvest on it are blocked and it keeps its state. Body <c>reason</c> (required), <c>reasonCode</c> (optional) and the raft <c>rowVersion</c>. 200 with
    /// <c>changed: false</c> when it is already damaged; 409 MSG-GEN-09 when the lane is out of service or removed.
    /// </summary>
    [HttpPost("api/rafts/{raftId:guid}/lanes/{laneNumber:int}/report-damage")]
    [ProducesResponseType<LaneIncidentSaved>(StatusCodes.Status200OK)]
    public async Task<IActionResult> ReportDamage(Guid raftId, int laneNumber, ReportLaneDamageRequest request, CancellationToken cancellationToken) =>
        (await commands.ReportDamageAsync(raftId, laneNumber, request, cancellationToken)).ToActionResult(Ok);

    /// <summary>
    /// Records the repair of a damaged lane: <c>reason</c> (what was done) and optional <c>materials</c> (<c>itemId</c>, <c>quantity</c>, <c>unit</c>), each checked against the stock on hand
    /// (409 MSG-INPUT-01) and taken out of it in the same save; the lane returns to the state it had. 409 <c>LANE_NOT_DAMAGED</c> when it is not damaged.
    /// <c>/repair-done</c> is the same action under the name of the first draft.
    /// </summary>
    [HttpPost("api/rafts/{raftId:guid}/lanes/{laneNumber:int}/repair")]
    [HttpPost("api/rafts/{raftId:guid}/lanes/{laneNumber:int}/repair-done")]
    [ProducesResponseType<LaneIncidentSaved>(StatusCodes.Status200OK)]
    public async Task<IActionResult> Repair(Guid raftId, int laneNumber, RepairLaneRequest request, CancellationToken cancellationToken) =>
        (await commands.RepairAsync(raftId, laneNumber, request, cancellationToken)).ToActionResult(Ok);

    /// <summary>
    /// Puts the lane out of service (ngừng hoạt động): left out of every list, its number never reused. When seed hangs on it the first answer is 409 with <c>confirmFlag</c> <c>confirm</c>; send
    /// <c>confirm: true</c> to record the loss of that seed (no quantity). The lane then counts as finished when the cycle closes. 200 with <c>changed: false</c> when it already is.
    /// </summary>
    [HttpPost("api/rafts/{raftId:guid}/lanes/{laneNumber:int}/out-of-service")]
    [ProducesResponseType<LaneIncidentSaved>(StatusCodes.Status200OK)]
    public async Task<IActionResult> OutOfService(Guid raftId, int laneNumber, LaneOutOfServiceRequest request, CancellationToken cancellationToken) =>
        (await commands.PutOutOfServiceAsync(raftId, laneNumber, request, cancellationToken)).ToActionResult(Ok);

    /// <summary>
    /// Brings a lane out of service back (<c>reason</c> optional, <c>materials</c> optional as for a repair): only when no Open cycle still holds seed on it, else 409 <c>MSG-GEN-09</c> with a Vietnamese text and
    /// <c>reasonCode</c> <c>LANE_HOLDS_SEED</c>. The lane is Not seeded for the next cycle; the history gets <c>Restored</c>.
    /// </summary>
    [HttpPost("api/rafts/{raftId:guid}/lanes/{laneNumber:int}/restore-service")]
    [ProducesResponseType<LaneIncidentSaved>(StatusCodes.Status200OK)]
    public async Task<IActionResult> RestoreService(Guid raftId, int laneNumber, RestoreLaneServiceRequest request, CancellationToken cancellationToken) =>
        (await commands.RestoreServiceAsync(raftId, laneNumber, request, cancellationToken)).ToActionResult(Ok);

    /// <summary>Adds an optional photo to an incident, at most three. Multipart field <c>file</c>, once: JPEG or PNG, at most 10 MB.</summary>
    [HttpPost("api/rafts/{raftId:guid}/lanes/{laneNumber:int}/incidents/{incidentId:guid}/photos")]
    [RequestSizeLimit(11 * 1024 * 1024)]
    [UploadField("Photo")]
    [ProducesResponseType<LaneIncidentView>(StatusCodes.Status201Created)]
    public async Task<IActionResult> AddPhoto(Guid raftId, int laneNumber, Guid incidentId, IFormFile? file, CancellationToken cancellationToken)
    {
        if (file is null)
        {
            return AppError.Invalid("Photo").ToProblem();
        }

        // The binder keeps the first of several parts named "file": refused, so no photo is dropped without a word.
        if (Request.Form.Files.GetFiles(nameof(file)).Count > 1)
        {
            return AppError.Invalid("Photo", "Send one photo per request.").ToProblem();
        }

        return (await photos.AddAsync(raftId, laneNumber, incidentId, await file.ToIncomingFileAsync(cancellationToken), cancellationToken))
            .ToActionResult(v => StatusCode(StatusCodes.Status201Created, v));
    }

    /// <summary>The history of one lane, newest first: who, when, what, why, the materials and the use issue of a repair.</summary>
    [HttpGet("api/rafts/{raftId:guid}/lanes/{laneNumber:int}/incidents")]
    [ProducesResponseType<LaneHistory>(StatusCodes.Status200OK)]
    public async Task<IActionResult> LaneHistory(Guid raftId, int laneNumber, CancellationToken cancellationToken) =>
        (await reads.HistoryAsync(raftId, laneNumber, cancellationToken)).ToActionResult(Ok);

    /// <summary>The history of every lane of the raft, or of one lane with <c>laneNumber</c>, newest first.</summary>
    [HttpGet("api/rafts/{raftId:guid}/lane-history")]
    [ProducesResponseType<LaneHistory>(StatusCodes.Status200OK)]
    public async Task<IActionResult> RaftHistory(Guid raftId, [FromQuery] int? laneNumber, CancellationToken cancellationToken) =>
        (await reads.HistoryAsync(raftId, laneNumber, cancellationToken)).ToActionResult(Ok);
}
