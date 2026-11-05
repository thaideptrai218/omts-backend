using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using OMTS.Api.Common;
using OMTS.Application.Features.WaterQuality;
using OMTS.Application.Features.WaterQuality.Dtos;

namespace OMTS.Api.Controllers;

/// <summary>
/// One water measurement by id (WATER-02 correct/void/editing indicator, WATER-03 detail). The route has no zone, so
/// each use case loads the record and checks the caller's WATER_MEASUREMENTS grant in its zone (View: detail, Edit:
/// correct/void/editing); another zone answers 403 MSG-GEN-08.
/// </summary>
[ApiController]
[Route("api/water-measurements/{id:guid}")]
[Authorize]
public sealed class WaterMeasurementsController(
    WaterMeasurementCorrectionService corrections,
    WaterMeasurementQueryService queries) : ControllerBase
{
    /// <summary>All fields, a short-lived photo URL, the thresholds of the measurement and who else is editing (MSG58).</summary>
    [HttpGet]
    [ProducesResponseType<WaterMeasurementDetail>(StatusCodes.Status200OK)]
    public async Task<IActionResult> Get(Guid id, CancellationToken cancellationToken) =>
        (await queries.GetAsync(id, cancellationToken)).ToActionResult(Ok);

    /// <summary>Corrects the values and the time of the same Active record, with a reason; its thresholds never change.</summary>
    [HttpPost("corrections")]
    [ProducesResponseType<WaterMeasurementDetail>(StatusCodes.Status200OK)]
    public async Task<IActionResult> Correct(Guid id, CorrectWaterMeasurementRequest request, CancellationToken cancellationToken) =>
        (await corrections.CorrectAsync(id, request, cancellationToken)).ToActionResult(Ok);

    /// <summary>Active → Voided, with a reason; final.</summary>
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
