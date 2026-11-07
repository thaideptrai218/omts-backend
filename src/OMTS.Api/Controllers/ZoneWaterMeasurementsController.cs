using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using OMTS.Api.Common;
using OMTS.Api.Filters;
using OMTS.Application.Features.Authorization;
using OMTS.Application.Features.Authorization.Models;
using OMTS.Application.Features.WaterQuality;
using OMTS.Application.Features.WaterQuality.Dtos;

namespace OMTS.Api.Controllers;

/// <summary>
/// WATER-01 record and WATER-03 view water measurements of one zone. Cooperative Owner everywhere; staff only in
/// zones where a grant gives WATER_MEASUREMENTS (View: history/chart, Create: record, Export: file).
/// </summary>
[ApiController]
[Route("api/zones/{zoneId:guid}/water-measurements")]
[Authorize]
public sealed class ZoneWaterMeasurementsController(
    WaterMeasurementRecordingService recording,
    WaterMeasurementQueryService queries) : ControllerBase
{
    // A 10 MB photo plus the multipart envelope; a larger photo still reaches the validator's MSG-GEN-01.
    private const long MaxUploadBytes = 12 * 1024 * 1024;

    /// <summary>
    /// Multipart: <c>photo</c> (JPEG/PNG, at most 10 MB). Stores the photo and reads it with OCR; no measurement is saved.
    /// Answers the <c>photoKey</c> to record with, the suggested values, the indicators not read (MSG-WATER-01) and the
    /// values outside the zone thresholds (MSG-WATER-02). When OCR fails, <c>ocr.status</c> is Failed and the key still comes back.
    /// </summary>
    [HttpPost("recognize")]
    [RequirePermission(PermissionFunctions.WaterMeasurements, PermissionAction.Create)]
    [RequestSizeLimit(MaxUploadBytes)]
    [UploadField(WaterMeasurementRecordingService.PhotoField)]
    [Consumes("multipart/form-data")]
    [ProducesResponseType<RecognitionResult>(StatusCodes.Status200OK)]
    public async Task<IActionResult> Recognize(Guid zoneId, IFormFile? photo, CancellationToken cancellationToken)
    {
        var file = photo is null ? null : await photo.ToIncomingFileAsync(cancellationToken);
        return (await recording.RecognizeAsync(zoneId, file, cancellationToken)).ToActionResult(Ok);
    }

    /// <summary>
    /// Saves the values the user confirmed as Active with a copy of the zone thresholds, and alerts when a value is
    /// outside them. 201 when created; 200 with the existing record when the <c>clientOperationId</c> was already saved.
    /// </summary>
    [HttpPost]
    [RequirePermission(PermissionFunctions.WaterMeasurements, PermissionAction.Create)]
    [Consumes("application/json")]
    [ProducesResponseType<WaterMeasurementDetail>(StatusCodes.Status201Created)]
    [ProducesResponseType<WaterMeasurementDetail>(StatusCodes.Status200OK)]
    public async Task<IActionResult> Record(Guid zoneId, RecordWaterMeasurementRequest request, CancellationToken cancellationToken) =>
        (await recording.RecordAsync(zoneId, request, cancellationToken))
            .ToActionResult(r => r.Replayed ? Ok(r.Measurement) : StatusCode(StatusCodes.Status201Created, r.Measurement));

    /// <summary>
    /// History: <c>from</c>/<c>to</c> local dates (yyyy-MM-dd, default the last 3 months), <c>status</c> (default both;
    /// Active, Voided or All when asked), newest first, 20 per page (at most 100). MSG-GEN-10 in <c>message</c> when empty.
    /// </summary>
    [HttpGet]
    [RequirePermission(PermissionFunctions.WaterMeasurements, PermissionAction.View)]
    [ProducesResponseType<WaterHistoryPage>(StatusCodes.Status200OK)]
    public async Task<IActionResult> History(
        Guid zoneId, string? from, string? to, [FromQuery] string[]? status, int page = 1, int pageSize = 20,
        CancellationToken cancellationToken = default) =>
        (await queries.SearchAsync(zoneId, new WaterHistoryQuery(from, to, status, page, pageSize), cancellationToken)).ToActionResult(Ok);

    /// <summary>Chart of the active measurements of the period as raw points (pH, salinity, temperature); aggregates stay on the client.</summary>
    [HttpGet("chart")]
    [RequirePermission(PermissionFunctions.WaterMeasurements, PermissionAction.View)]
    [ProducesResponseType<WaterChart>(StatusCodes.Status200OK)]
    public async Task<IActionResult> Chart(Guid zoneId, string? from, string? to, CancellationToken cancellationToken) =>
        (await queries.ChartAsync(zoneId, from, to, cancellationToken)).ToActionResult(Ok);

    /// <summary>Excel file (sheet "Measurements") with the history filters: voided ones with their reason, each row with its own thresholds.</summary>
    [HttpGet("export")]
    [RequirePermission(PermissionFunctions.WaterMeasurements, PermissionAction.View)]
    [RequirePermission(PermissionFunctions.WaterMeasurements, PermissionAction.Export)]
    [ProducesResponseType<FileContentResult>(StatusCodes.Status200OK)]
    public async Task<IActionResult> Export(
        Guid zoneId, string? from, string? to, [FromQuery] string[]? status, CancellationToken cancellationToken) =>
        (await queries.ExportAsync(zoneId, new WaterHistoryQuery(from, to, status), cancellationToken))
            .ToActionResult(file => File(file.Content, file.ContentType, file.FileName));
}
