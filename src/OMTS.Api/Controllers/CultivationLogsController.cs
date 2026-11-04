using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using OMTS.Api.Common;
using OMTS.Api.Filters;
using OMTS.Application.Features.Cultivation.CultivationLogs;
using OMTS.Application.Features.Files;

namespace OMTS.Api.Controllers;

/// <summary>
/// CYCLE-04 – cultivation logs of a cycle with their photos, for a Zone Manager of the raft's zone and the Cooperative Owner, not for an Accountant
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

    [HttpGet("api/cultivation-cycles/{cycleId:guid}/cultivation-logs")]
    [ProducesResponseType<IReadOnlyList<CultivationLogView>>(StatusCodes.Status200OK)]
    public async Task<IActionResult> List(Guid cycleId, CancellationToken cancellationToken) =>
        (await reads.ListAsync(cycleId, cancellationToken)).ToActionResult(Ok);

    /// <summary>
    /// Records a visit while the cycle is Open. Multipart: <c>visitDate</c>, optional <c>notes</c>, and one <c>photos</c> part per photo (1 to 10). A failed
    /// upload answers 502 MSG-GEN-03 and saves nothing. Notifies the Cooperative Owner once (MSG-CYCLE-04).
    /// </summary>
    [HttpPost("api/cultivation-cycles/{cycleId:guid}/cultivation-logs")]
    [RequestSizeLimit(MaxUploadBytes)]
    [UploadField(PhotosField)]
    [Consumes("multipart/form-data")]
    [ProducesResponseType<CultivationLogSaved>(StatusCodes.Status201Created)]
    public async Task<IActionResult> Record(Guid cycleId, [FromForm] CultivationLogForm form, CancellationToken cancellationToken)
    {
        InvariantFormValues.TryDate(form.VisitDate, out var visitDate);
        var request = new RecordCultivationLogRequest(visitDate, form.Notes, OtherFilePart());
        return (await logs.RecordAsync(cycleId, request, await PhotosAsync(form, cancellationToken), cancellationToken))
            .ToActionResult(r => StatusCode(StatusCodes.Status201Created, r));
    }

    [HttpGet("api/cultivation-logs/{logId:guid}")]
    [ProducesResponseType<CultivationLogView>(StatusCodes.Status200OK)]
    public async Task<IActionResult> Get(Guid logId, CancellationToken cancellationToken) =>
        (await reads.GetAsync(logId, cancellationToken)).ToActionResult(Ok);

    /// <summary>
    /// Corrects a log while the cycle is Open: the recording Zone Manager or the Cooperative Owner. Multipart: <c>visitDate</c>, <c>notes</c>, one
    /// <c>keepPhotoKeys</c> field per photo to keep, one <c>photos</c> part per new photo and <c>reason</c>. No version: the last save wins (NFR-09). Notifies nobody.
    /// </summary>
    [HttpPut("api/cultivation-logs/{logId:guid}")]
    [RequestSizeLimit(MaxUploadBytes)]
    [UploadField(PhotosField)]
    [Consumes("multipart/form-data")]
    [ProducesResponseType<CultivationLogSaved>(StatusCodes.Status200OK)]
    public async Task<IActionResult> Correct(Guid logId, [FromForm] CultivationLogForm form, CancellationToken cancellationToken)
    {
        InvariantFormValues.TryDate(form.VisitDate, out var visitDate);
        var request = new CorrectCultivationLogRequest(visitDate, form.Notes, form.KeepPhotoKeys, form.Reason, OtherFilePart());
        return (await corrections.CorrectAsync(logId, request, await PhotosAsync(form, cancellationToken), cancellationToken)).ToActionResult(Ok);
    }

    /// <summary>Removes a log and its photos while the cycle is Open; ask with MSG-GEN-04 first. 204; the audit log keeps it with the reason.</summary>
    [HttpPost("api/cultivation-logs/{logId:guid}/remove")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    public async Task<IActionResult> Remove(Guid logId, RemoveCultivationLogRequest request, CancellationToken cancellationToken) =>
        (await corrections.RemoveAsync(logId, request, cancellationToken)).ToActionResult(_ => NoContent());

    /// <summary>A file sent under another name would be dropped without a word. The service refuses it, after its access check (GFR-02), so no photo is lost.</summary>
    private bool OtherFilePart() =>
        Request.Form.Files.Any(f => !f.Name.Equals(nameof(CultivationLogForm.Photos), StringComparison.OrdinalIgnoreCase));

    private static async Task<IReadOnlyList<IncomingFile>> PhotosAsync(CultivationLogForm form, CancellationToken cancellationToken)
    {
        var incoming = new List<IncomingFile>();
        foreach (var photo in form.Photos ?? [])
        {
            incoming.Add(await photo.ToIncomingFileAsync(cancellationToken));
        }

        return incoming;
    }
}
