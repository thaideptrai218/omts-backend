using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using OMTS.Api.Common;
using OMTS.Api.Filters;
using OMTS.Application.Common.Results;
using OMTS.Application.Features.AccessManagement.Dtos;
using OMTS.Application.Features.Authorization;
using OMTS.Application.Features.Authorization.Models;
using OMTS.Application.Features.WaterQuality;
using OMTS.Application.Features.WaterQuality.Dtos;

namespace OMTS.Api.Controllers;

/// <summary>
/// ENV-01 record and ENV-03 view water measurements of one zone. Cooperative Owner everywhere; staff only in
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
    /// Multipart: <c>photo</c> (JPEG/PNG, at most 10 MB) and an optional <c>clientOperationId</c>. Stores the photo,
    /// reads it with OCR and saves a Draft; when OCR fails the Draft is still saved and <c>ocr.status</c> is Failed (MSG-WATER-01).
    /// </summary>
    [HttpPost("recognize")]
    [RequirePermission(PermissionFunctions.WaterMeasurements, PermissionAction.Create)]
    [RequestSizeLimit(MaxUploadBytes)]
    [UploadField(WaterMeasurementRecordingService.PhotoField)]
    [Consumes("multipart/form-data")]
    [ProducesResponseType<RecognitionResult>(StatusCodes.Status200OK)]
    public async Task<IActionResult> Recognize(Guid zoneId, IFormFile? photo, [FromForm] string? clientOperationId, CancellationToken cancellationToken)
    {
        if (!InvariantFormValues.TryGuid(clientOperationId, out var operationId))
        {
            return AppError.Invalid(WaterMeasurementRecordingService.ClientOperationField).ToProblem();
        }

        var file = photo is null ? null : await photo.ToIncomingFileAsync(cancellationToken);
        return (await recording.RecognizeAsync(zoneId, file, operationId, cancellationToken)).ToActionResult(Ok);
    }

    /// <summary>
    /// Manual entry / offline sync (JSON). <c>confirm=true</c> saves a Confirmed record and checks the thresholds.
    /// 201 when created; 200 with the existing record when the <c>clientOperationId</c> was already saved.
    /// </summary>
    [HttpPost]
    [RequirePermission(PermissionFunctions.WaterMeasurements, PermissionAction.Create)]
    [Consumes("application/json")]
    [ProducesResponseType<WaterMeasurementDetail>(StatusCodes.Status201Created)]
    [ProducesResponseType<WaterMeasurementDetail>(StatusCodes.Status200OK)]
    public async Task<IActionResult> Record(Guid zoneId, RecordWaterMeasurementRequest request, CancellationToken cancellationToken) =>
        Recorded(await recording.RecordAsync(zoneId, request, null, cancellationToken));

    /// <summary>
    /// The same as the JSON variant as multipart, with an optional <c>photo</c> (offline sync of a photographed
    /// measurement); numbers use a dot, times are ISO 8601 (Vietnam time, UTC+7, when no offset is given).
    /// </summary>
    [HttpPost("with-photo")]
    [RequirePermission(PermissionFunctions.WaterMeasurements, PermissionAction.Create)]
    [RequestSizeLimit(MaxUploadBytes)]
    [UploadField(WaterMeasurementRecordingService.PhotoField)]
    [Consumes("multipart/form-data")]
    [ProducesResponseType<WaterMeasurementDetail>(StatusCodes.Status201Created)]
    [ProducesResponseType<WaterMeasurementDetail>(StatusCodes.Status200OK)]
    public async Task<IActionResult> RecordWithPhoto(Guid zoneId, [FromForm] RecordWaterMeasurementForm form, CancellationToken cancellationToken)
    {
        var invalid = new List<string>();
        if (!InvariantFormValues.TryGuid(form.ClientOperationId, out var operationId))
        {
            invalid.Add(WaterMeasurementRecordingService.ClientOperationField);
        }

        if (!InvariantFormValues.TryDecimal(form.Ph, out var ph))
        {
            invalid.Add(WaterValueRules.PhField);
        }

        if (!InvariantFormValues.TryDecimal(form.Salinity, out var salinity))
        {
            invalid.Add(WaterValueRules.SalinityField);
        }

        if (!InvariantFormValues.TryDateTimeOffset(form.MeasuredAt, out var measuredAt))
        {
            invalid.Add(WaterValueRules.MeasuredAtField);
        }

        if (!InvariantFormValues.TryBoolean(form.Confirm, out var confirm))
        {
            invalid.Add("Confirm");
        }

        if (invalid.Count > 0)
        {
            return AppError.InvalidFields(invalid).ToProblem();
        }

        var file = form.Photo is null ? null : await form.Photo.ToIncomingFileAsync(cancellationToken);
        var request = new RecordWaterMeasurementRequest(operationId, measuredAt, ph, salinity, confirm);
        return Recorded(await recording.RecordAsync(zoneId, request, file, cancellationToken));
    }

    /// <summary>
    /// History: <c>from</c>/<c>to</c> local dates (yyyy-MM-dd, default the last 3 months), <c>status</c> (default
    /// Confirmed; Draft, Corrected, Voided or All when asked), newest first, 20 per page (at most 100).
    /// </summary>
    [HttpGet]
    [RequirePermission(PermissionFunctions.WaterMeasurements, PermissionAction.View)]
    [ProducesResponseType<Paged<WaterMeasurementListItem>>(StatusCodes.Status200OK)]
    public async Task<IActionResult> History(
        Guid zoneId, string? from, string? to, [FromQuery] string[]? status, int page = 1, int pageSize = 20,
        CancellationToken cancellationToken = default) =>
        (await queries.SearchAsync(zoneId, new WaterHistoryQuery(from, to, status, page, pageSize), cancellationToken)).ToActionResult(Ok);

    /// <summary>Chart of the confirmed values: <c>groupBy</c> = month (default, last 12 months) or day (at most 366 days).</summary>
    [HttpGet("chart")]
    [RequirePermission(PermissionFunctions.WaterMeasurements, PermissionAction.View)]
    [ProducesResponseType<WaterChart>(StatusCodes.Status200OK)]
    public async Task<IActionResult> Chart(Guid zoneId, string? from, string? to, string? groupBy, CancellationToken cancellationToken) =>
        (await queries.ChartAsync(zoneId, from, to, groupBy, cancellationToken)).ToActionResult(Ok);

    /// <summary>Excel file (sheets "Measurements" and "Monthly summary") with the history filters; Drafts and voided records are never exported.</summary>
    [HttpGet("export")]
    [RequirePermission(PermissionFunctions.WaterMeasurements, PermissionAction.View)]
    [RequirePermission(PermissionFunctions.WaterMeasurements, PermissionAction.Export)]
    [ProducesResponseType<FileContentResult>(StatusCodes.Status200OK)]
    public async Task<IActionResult> Export(
        Guid zoneId, string? from, string? to, [FromQuery] string[]? status, CancellationToken cancellationToken) =>
        (await queries.ExportAsync(zoneId, new WaterHistoryQuery(from, to, status), cancellationToken))
            .ToActionResult(file => File(file.Content, file.ContentType, file.FileName));

    private IActionResult Recorded(Result<RecordedWaterMeasurement> result) =>
        result.ToActionResult(r => r.Replayed ? Ok(r.Measurement) : StatusCode(StatusCodes.Status201Created, r.Measurement));
}
