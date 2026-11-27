using System.Globalization;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using OMTS.Api.Common;
using OMTS.Api.Filters;
using OMTS.Application.Features.Cultivation.CultivationLogs;
using OMTS.Application.Features.Files;

namespace OMTS.Api.Controllers;

/// <summary>
/// CYCLE-04 – cultivation logs of a cycle or of a raft with their photos, for a Zone Manager of the raft's zone and the Cooperative Owner, not for an Accountant
/// (§I.4.4). The routes have no zone: the services check the zone of the raft.
/// </summary>
[ApiController]
[Authorize]
public sealed class CultivationLogsController(
    CultivationLogReadService reads, CultivationLogService logs, CultivationLogCorrectionService corrections) : ControllerBase
{
    private const string PhotosField = "Photos";

    // Ten photos of 10 MB plus the multipart envelope; a larger body answers MSG-GEN-01 on the photos.
    private const long MaxUploadBytes = 105 * 1024 * 1024;

    /// <summary>The logs of the cycle, the latest visit first. Optional <c>logType</c> keeps one type; an unknown one answers 400 MSG-GEN-01 on <c>LogType</c>.</summary>
    [HttpGet("api/cultivation-cycles/{cycleId:guid}/cultivation-logs")]
    [ProducesResponseType<IReadOnlyList<CultivationLogView>>(StatusCodes.Status200OK)]
    public async Task<IActionResult> List(Guid cycleId, [FromQuery] string? logType, CancellationToken cancellationToken) =>
        (await reads.ListAsync(cycleId, logType, cancellationToken)).ToActionResult(Ok);

    /// <summary>The logs of the raft in every cycle it had and in none, the latest visit first. Optional <c>logType</c> keeps one type; an unknown one answers 400 MSG-GEN-01 on <c>LogType</c>.</summary>
    [HttpGet("api/rafts/{raftId:guid}/cultivation-logs")]
    [ProducesResponseType<IReadOnlyList<CultivationLogView>>(StatusCodes.Status200OK)]
    public async Task<IActionResult> ListByRaft(Guid raftId, [FromQuery] string? logType, CancellationToken cancellationToken) =>
        (await reads.ListByRaftAsync(raftId, logType, cancellationToken)).ToActionResult(Ok);

    /// <summary>
    /// Records a visit while the cycle is Open. Multipart: <c>visitDate</c>, optional <c>logType</c> (FEEDING, CLEANING, INSPECTION, DISEASE, REPAIR or OTHER; default OTHER), optional <c>notes</c>, and one <c>photos</c> part per photo (1 to 10). A failed
    /// upload answers 502 MSG-GEN-03 and saves nothing. Notifies the Cooperative Owner once (MSG-CYCLE-04).
    /// </summary>
    [HttpPost("api/cultivation-cycles/{cycleId:guid}/cultivation-logs")]
    [RequestSizeLimit(MaxUploadBytes)]
    [UploadField(PhotosField)]
    [Consumes("multipart/form-data")]
    [ProducesResponseType<CultivationLogSaved>(StatusCodes.Status201Created)]
    public async Task<IActionResult> Record(Guid cycleId, [FromForm] CultivationLogForm form, CancellationToken cancellationToken)
    {
        var visitDate = ParseVisitDate(form.VisitDate);
        var request = new RecordCultivationLogRequest(visitDate, form.Notes, OtherFilePart(), form.LogType);
        return (await logs.RecordAsync(cycleId, request, await form.Photos.ToIncomingFilesAsync(cancellationToken), cancellationToken))
            .ToActionResult(r => StatusCode(StatusCodes.Status201Created, r));
    }

    /// <summary>
    /// Records a visit to the raft, which may have no cultivation cycle or be Discontinued. Same multipart body as the cycle route. The cycle is worked out from <c>visitDate</c> (not after today): the Open cycle it falls in,
    /// none when it falls in no cycle, and a date inside a Closed cycle answers 409 MSG-GEN-09. A raft In Use or Discontinued in a zone In Use takes logs. Notifies the Cooperative Owner once (MSG-CYCLE-04).
    /// </summary>
    [HttpPost("api/rafts/{raftId:guid}/cultivation-logs")]
    [RequestSizeLimit(MaxUploadBytes)]
    [UploadField(PhotosField)]
    [Consumes("multipart/form-data")]
    [ProducesResponseType<CultivationLogSaved>(StatusCodes.Status201Created)]
    public async Task<IActionResult> RecordOnRaft(Guid raftId, [FromForm] CultivationLogForm form, CancellationToken cancellationToken)
    {
        var visitDate = ParseVisitDate(form.VisitDate);
        var request = new RecordCultivationLogRequest(visitDate, form.Notes, OtherFilePart(), form.LogType);
        return (await logs.RecordOnRaftAsync(raftId, request, await form.Photos.ToIncomingFilesAsync(cancellationToken), cancellationToken))
            .ToActionResult(r => StatusCode(StatusCodes.Status201Created, r));
    }

    /// <summary>
    /// The logs of every raft the caller may read (the "Nhật ký" screen), the latest visit first unless <c>sortBy</c> (<c>visitDate</c>, <c>zone</c>, <c>raft</c>, <c>logType</c>, <c>recordedBy</c>, <c>recordedAt</c>) and <c>sortDir</c> (<c>asc</c>, <c>desc</c>) say otherwise: query <c>zoneId</c>, <c>raftId</c>, <c>logType</c>, <c>from</c> and <c>to</c> (visit dates, <c>yyyy-MM-dd</c>, inclusive),
    /// <c>search</c> (raft code or notes), <c>page</c> and <c>pageSize</c> (at most 100). A zone the caller cannot read answers 403.
    /// </summary>
    [HttpGet("api/cultivation-logs")]
    [ProducesResponseType<CultivationLogPage>(StatusCodes.Status200OK)]
    public async Task<IActionResult> Search([FromQuery] CultivationLogListQuery query, CancellationToken cancellationToken) =>
        (await reads.SearchAsync(query, cancellationToken)).ToActionResult(Ok);

    [HttpGet("api/cultivation-logs/{logId:guid}")]
    [ProducesResponseType<CultivationLogView>(StatusCodes.Status200OK)]
    public async Task<IActionResult> Get(Guid logId, CancellationToken cancellationToken) =>
        (await reads.GetAsync(logId, cancellationToken)).ToActionResult(Ok);

    /// <summary>
    /// Corrects a log while its cycle, if it has one, is Open (the raft In Use or Discontinued); the cycle is worked out from <c>visitDate</c> again, and a date inside a Closed cycle answers 409 MSG-GEN-09: the recording Zone Manager or the Cooperative Owner. Multipart: <c>visitDate</c>, <c>logType</c>, <c>notes</c>, one
    /// <c>keepPhotoKeys</c> field per photo to keep, one <c>photos</c> part per new photo and <c>reason</c>. No version: the last save wins (NFR-09). Notifies nobody.
    /// </summary>
    [HttpPut("api/cultivation-logs/{logId:guid}")]
    [RequestSizeLimit(MaxUploadBytes)]
    [UploadField(PhotosField)]
    [Consumes("multipart/form-data")]
    [ProducesResponseType<CultivationLogSaved>(StatusCodes.Status200OK)]
    public async Task<IActionResult> Correct(Guid logId, [FromForm] CultivationLogForm form, CancellationToken cancellationToken)
    {
        var visitDate = ParseVisitDate(form.VisitDate);
        var request = new CorrectCultivationLogRequest(visitDate, form.Notes, form.KeepPhotoKeys, form.Reason, OtherFilePart(), form.LogType);
        return (await corrections.CorrectAsync(logId, request, await form.Photos.ToIncomingFilesAsync(cancellationToken), cancellationToken)).ToActionResult(Ok);
    }

    /// <summary>Removes a log and its photos while its cycle, if it has one, is Open; ask with MSG-GEN-04 first. 204; the audit log keeps it with the reason.</summary>
    [HttpPost("api/cultivation-logs/{logId:guid}/remove")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    public async Task<IActionResult> Remove(Guid logId, RemoveCultivationLogRequest request, CancellationToken cancellationToken) =>
        (await corrections.RemoveAsync(logId, request, cancellationToken)).ToActionResult(_ => NoContent());

    private static DateOnly? ParseVisitDate(string? value) =>
        DateOnly.TryParseExact(value?.Trim(), "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date)
            ? date
            : null;

    /// <summary>A file sent under another name would be dropped without a word. The service refuses it, after its access check (GFR-02), so no photo is lost.</summary>
    private bool OtherFilePart() => Request.Form.Files.HasPartOtherThan(nameof(CultivationLogForm.Photos));
}
