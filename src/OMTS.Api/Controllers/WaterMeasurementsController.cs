using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using OMTS.Api.Common;
using OMTS.Application.Features.WaterQuality;
using OMTS.Application.Features.WaterQuality.Dtos;

namespace OMTS.Api.Controllers;

/// <summary>
/// One water measurement by id (WATER-02 void, WATER-03 detail). The route has no zone, so each use case loads
/// the record and checks the caller's WATER_MEASUREMENTS grant in its zone (View: detail, Edit: void); another
/// zone answers 403 MSG-GEN-08. A measurement is never corrected, only voided. Water has no version check and no editing notice (NFR-09, NFR-13).
/// </summary>
[ApiController]
[Route("api/water-measurements/{id:guid}")]
[Authorize]
public sealed class WaterMeasurementsController(
    WaterMeasurementCorrectionService voids,
    WaterMeasurementQueryService queries) : ControllerBase
{
    /// <summary>All fields, a short-lived photo URL and the thresholds of the measurement.</summary>
    [HttpGet]
    [ProducesResponseType<WaterMeasurementDetail>(StatusCodes.Status200OK)]
    public async Task<IActionResult> Get(Guid id, CancellationToken cancellationToken) =>
        (await queries.GetAsync(id, cancellationToken)).ToActionResult(Ok);

    /// <summary>Active → Voided, with a reason; final.</summary>
    [HttpPost("void")]
    [ProducesResponseType<WaterMeasurementDetail>(StatusCodes.Status200OK)]
    public async Task<IActionResult> Void(Guid id, VoidWaterMeasurementRequest request, CancellationToken cancellationToken) =>
        (await voids.VoidAsync(id, request, cancellationToken)).ToActionResult(Ok);
}
