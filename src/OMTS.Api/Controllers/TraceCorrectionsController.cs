using System.Globalization;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using OMTS.Api.Common;
using OMTS.Api.Filters;
using OMTS.Application.Features.Cultivation.CultivationLogs;
using OMTS.Application.Features.Files;
using OMTS.Application.Features.Harvest;
using OMTS.Application.Features.Traceability.Corrections;
using static OMTS.Api.Common.FormNumbers;

namespace OMTS.Api.Controllers;

/// <summary>
/// TRACE-03 – correct traceability data (SCR-TRACE-02), for a Zone Manager of the raft's zone and the Cooperative Owner, not for an Accountant (§I.4.4, PROVENANCE). Only the
/// records the cultivation and harvest screens no longer allow the Zone Manager to change (BR-TRACE-04). A Zone Manager saves with <c>ownerApproved: true</c> after MSG-TRACE-04,
/// else 409 MSG-TRACE-04 and nothing changes; the Owner confirms with MSG-GEN-04 on the client. The routes have no zone: the services check the zone of the raft.
/// </summary>
[ApiController]
[Authorize]
public sealed class TraceCorrectionsController(
    TraceCorrectionReadService reads,
    TraceSeedingCorrectionService seedings,
    TraceCultivationLogCorrectionService logs,
    TraceHarvestLotCorrectionService lots) : ControllerBase
{
    private const string PhotosField = "Photos";

    // Ten photos of 10 MB plus the multipart envelope; a larger body answers MSG-GEN-01 on the photos.
    private const long MaxUploadBytes = 105 * 1024 * 1024;

    /// <summary>The cycles of the raft, Open and Closed, the newest first (TRACE-03.0 step 1).</summary>
    [HttpGet("api/rafts/{raftId:guid}/trace-corrections")]
    [ProducesResponseType<TraceCorrectionCycles>(StatusCodes.Status200OK)]
    public async Task<IActionResult> Cycles(Guid raftId, CancellationToken cancellationToken) =>
        (await reads.CyclesOfRaftAsync(raftId, cancellationToken)).ToActionResult(Ok);

    /// <summary>The records of the cycle that can be corrected only here (step 3); empty lists with MSG-GEN-10 when there is none.</summary>
    [HttpGet("api/cultivation-cycles/{cycleId:guid}/trace-corrections")]
    [ProducesResponseType<TraceCorrectionRecords>(StatusCodes.Status200OK)]
    public async Task<IActionResult> Records(Guid cycleId, CancellationToken cancellationToken) =>
        (await reads.RecordsOfCycleAsync(cycleId, cancellationToken)).ToActionResult(Ok);

    /// <summary>Steps 5 and 6 for a seeding session: the same checks as the save, the values before and after, the lots whose origin information changes. Changes nothing.</summary>
    [HttpPost("api/seeding-records/{recordId:guid}/trace-correction/preview")]
    [ProducesResponseType<TraceCorrectionPreview>(StatusCodes.Status200OK)]
    public async Task<IActionResult> PreviewSeeding(Guid recordId, TraceSeedingCorrectionRequest request, CancellationToken cancellationToken) =>
        (await seedings.PreviewAsync(recordId, request, cancellationToken)).ToActionResult(Ok);

    /// <summary>Corrects a seeding session (TRACE-03.0): date, seed lot, lanes, quantity, reason and version. 400 MSG-TRACE-03 when a lane of a lot would have no seed lot.</summary>
    [HttpPut("api/seeding-records/{recordId:guid}/trace-correction")]
    [ProducesResponseType<TraceSeedingCorrected>(StatusCodes.Status200OK)]
    public async Task<IActionResult> CorrectSeeding(Guid recordId, TraceSeedingCorrectionRequest request, CancellationToken cancellationToken) =>
        (await seedings.CorrectAsync(recordId, request, cancellationToken)).ToActionResult(Ok);

    /// <summary>Step 6 for a cultivation log of a Closed cycle: the log as it is and what to ask; no harvest lot is listed (BR-TRACE-05).</summary>
    [HttpGet("api/cultivation-logs/{logId:guid}/trace-correction/preview")]
    [ProducesResponseType<TraceCorrectionPreview>(StatusCodes.Status200OK)]
    public async Task<IActionResult> PreviewLog(Guid logId, CancellationToken cancellationToken) =>
        (await logs.PreviewAsync(logId, cancellationToken)).ToActionResult(Ok);

    /// <summary>
    /// Corrects a cultivation log of a Closed cycle (TRACE-03.1). Multipart: <c>visitDate</c>, <c>notes</c>, one <c>keepPhotoKeys</c> field per photo to keep, one <c>photos</c> part per
    /// new photo, <c>reason</c> and <c>ownerApproved</c>. No version: the last save wins (NFR-09). A failed upload answers 502 MSG-GEN-03 and saves nothing.
    /// </summary>
    [HttpPut("api/cultivation-logs/{logId:guid}/trace-correction")]
    [RequestSizeLimit(MaxUploadBytes)]
    [UploadField(PhotosField)]
    [Consumes("multipart/form-data")]
    [ProducesResponseType<CultivationLogSaved>(StatusCodes.Status200OK)]
    public async Task<IActionResult> CorrectLog(Guid logId, [FromForm] TraceCultivationLogForm form, CancellationToken cancellationToken)
    {
        var visitDate = DateOnly.TryParseExact(form.VisitDate?.Trim(), "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date) ? date : (DateOnly?)null;
        var request = new CorrectCultivationLogRequest(visitDate, form.Notes, form.KeepPhotoKeys, form.Reason, Request.Form.Files.HasPartOtherThan(nameof(CultivationLogForm.Photos)));
        return (await logs.CorrectAsync(logId, request, form.OwnerApproved, await form.Photos.ToIncomingFilesAsync(cancellationToken), cancellationToken)).ToActionResult(Ok);
    }

    /// <summary>Steps 5 and 6 for the harvest data of a lot of a Closed cycle: the same checks as the save but the version and new photos. Changes nothing.</summary>
    [HttpPost("api/harvest-lots/{lotId:guid}/trace-correction/preview")]
    [ProducesResponseType<TraceCorrectionPreview>(StatusCodes.Status200OK)]
    public async Task<IActionResult> PreviewLot(Guid lotId, TraceHarvestLotPreviewRequest request, CancellationToken cancellationToken) =>
        (await lots.PreviewAsync(lotId, request, cancellationToken)).ToActionResult(Ok);

    /// <summary>
    /// Corrects the harvest data of a lot of a Closed cycle (TRACE-03.2). Multipart: the fields of a lot correction (<c>laneNumbers</c>, <c>finishedLaneNumbers</c>, <c>cageCount</c>,
    /// <c>estimatedKg</c>, <c>keepPhotoKeys</c>, <c>photos</c>, <c>reason</c>, <c>rowVersion</c>) and <c>ownerApproved</c>. The harvest date and time never change.
    /// </summary>
    [HttpPut("api/harvest-lots/{lotId:guid}/trace-correction")]
    [RequestSizeLimit(MaxUploadBytes)]
    [UploadField(PhotosField)]
    [Consumes("multipart/form-data")]
    [ProducesResponseType<TraceHarvestLotCorrected>(StatusCodes.Status200OK)]
    public async Task<IActionResult> CorrectLot(Guid lotId, [FromForm] TraceHarvestLotForm form, CancellationToken cancellationToken)
    {
        TryInt(form.CageCount, out var cageCount);
        var estimatedIsRead = TryDecimal(form.EstimatedKg, out var estimatedKg);
        var request = new CorrectHarvestLotRequest(
            Numbers(form.LaneNumbers), Numbers(form.FinishedLaneNumbers), cageCount, estimatedKg, form.KeepPhotoKeys, form.Reason, form.RowVersion, !estimatedIsRead,
            Request.Form.Files.HasPartOtherThan(nameof(HarvestLotForm.Photos)));
        return (await lots.CorrectAsync(lotId, request, form.OwnerApproved, await form.Photos.ToIncomingFilesAsync(cancellationToken), cancellationToken)).ToActionResult(Ok);
    }
}
