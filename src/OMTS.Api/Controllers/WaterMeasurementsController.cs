using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using OMTS.Api.Common;
using OMTS.Application.Features.WaterQuality;
using OMTS.Application.Features.WaterQuality.Dtos;

namespace OMTS.Api.Controllers;

/// <summary>
/// One water measurement by id (ENV-01 confirm, ENV-02 correct/void/editing indicator, ENV-03 detail). The route
/// has no zone, so each use case loads the record and checks the caller's WATER_MEASUREMENTS grant in its zone
/// (View: detail, Create: confirm/recognize, Edit: correct/void/editing); another zone answers 403 MSG-GEN-08.
/// </summary>
[ApiController]
[Route("api/water-measurements/{id:guid}")]
[Authorize]
public sealed class WaterMeasurementsController(
    WaterMeasurementRecordingService recording,
    WaterMeasurementCorrectionService corrections,
    WaterMeasurementQueryService queries) : ControllerBase
{
    /// <summary>All fields, a short-lived photo URL, the OCR draft, thresholds used, the version chain and who else is editing (MSG58).</summary>
    [HttpGet]
    [ProducesResponseType<WaterMeasurementDetail>(StatusCodes.Status200OK)]
    public async Task<IActionResult> Get(Guid id, CancellationToken cancellationToken) =>
        (await queries.GetAsync(id, cancellationToken)).ToActionResult(Ok);

    /// <summary>Draft → Confirmed with the final values (omitted ones keep the draft's); a value outside the thresholds raises the alert (MSG27).</summary>
    [HttpPost("confirm")]
    [ProducesResponseType<WaterMeasurementDetail>(StatusCodes.Status200OK)]
    public async Task<IActionResult> Confirm(Guid id, ConfirmWaterMeasurementRequest request, CancellationToken cancellationToken) =>
        (await recording.ConfirmAsync(id, request, cancellationToken)).ToActionResult(Ok);

    /// <summary>Sends the photo of a Draft without OCR values (kept offline) to OCR again.</summary>
    [HttpPost("recognize")]
    [ProducesResponseType<RecognitionResult>(StatusCodes.Status200OK)]
    public async Task<IActionResult> Recognize(Guid id, CancellationToken cancellationToken) =>
        (await recording.RecognizeAgainAsync(id, cancellationToken)).ToActionResult(Ok);

    /// <summary>
    /// Saves a correction of the latest Confirmed version as a new version (201); the old one becomes Corrected.
    /// An outdated <c>recordVersion</c> gives 409 MSG06 with <c>latestVersion</c>.
    /// </summary>
    [HttpPost("corrections")]
    [ProducesResponseType<WaterMeasurementDetail>(StatusCodes.Status201Created)]
    public async Task<IActionResult> Correct(Guid id, CorrectWaterMeasurementRequest request, CancellationToken cancellationToken) =>
        (await corrections.CorrectAsync(id, request, cancellationToken)).ToActionResult(m => StatusCode(StatusCodes.Status201Created, m));

    /// <summary>Draft or the latest Confirmed version → Voided, with a reason.</summary>
    [HttpPost("void")]
    [ProducesResponseType<WaterMeasurementDetail>(StatusCodes.Status200OK)]
    public async Task<IActionResult> Void(Guid id, VoidWaterMeasurementRequest request, CancellationToken cancellationToken) =>
        (await corrections.VoidAsync(id, request, cancellationToken)).ToActionResult(Ok);

    /// <summary>Starts editing or keeps it alive (send at least every 5 minutes); returns the other editors (MSG58). Never locks.</summary>
    [HttpPost("editing")]
    [ProducesResponseType<EditingStatus>(StatusCodes.Status200OK)]
    public async Task<IActionResult> StartEditing(Guid id, CancellationToken cancellationToken) =>
        (await corrections.StartEditingAsync(id, cancellationToken)).ToActionResult(Ok);

    [HttpDelete("editing")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    public async Task<IActionResult> StopEditing(Guid id, CancellationToken cancellationToken) =>
        (await corrections.StopEditingAsync(id, cancellationToken)).ToActionResult(_ => NoContent());
}
