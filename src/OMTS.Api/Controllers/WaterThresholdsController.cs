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
/// WATER-04 – water thresholds per zone (pH, salinity, temperature). Only the Cooperative Owner sets them; staff holding
/// View on WATER_MEASUREMENTS read those of their zones. A minimum not less than its maximum is refused (MSG-WATER-05),
/// and a Discontinued zone is read-only (MSG-GEN-09).
/// </summary>
[ApiController]
[Authorize]
public sealed class WaterThresholdsController(WaterThresholdService thresholds) : ControllerBase
{
    /// <summary>Thresholds of every In Use zone the caller may view.</summary>
    [HttpGet("api/water-thresholds")]
    [RequirePermission(PermissionFunctions.WaterMeasurements, PermissionAction.View, PermissionScope.AnyZone)]
    [ProducesResponseType<IReadOnlyList<ZoneWaterThresholds>>(StatusCodes.Status200OK)]
    public async Task<IActionResult> List(CancellationToken cancellationToken) =>
        Ok(await thresholds.ListAsync(cancellationToken));

    /// <summary>The same thresholds for every In Use zone (cooperative-wide default); one audit row per changed zone.</summary>
    [HttpPut("api/water-thresholds")]
    [RequireCooperativeOwner]
    [ProducesResponseType<IReadOnlyList<ZoneWaterThresholds>>(StatusCodes.Status200OK)]
    public async Task<IActionResult> SetForAllZones(SetWaterThresholdsRequest request, CancellationToken cancellationToken) =>
        (await thresholds.SetForAllInUseZonesAsync(request, cancellationToken)).ToActionResult(Ok);

    [HttpGet("api/zones/{zoneId:guid}/water-thresholds")]
    [RequirePermission(PermissionFunctions.WaterMeasurements, PermissionAction.View)]
    [ProducesResponseType<ZoneWaterThresholds>(StatusCodes.Status200OK)]
    public async Task<IActionResult> Get(Guid zoneId, CancellationToken cancellationToken) =>
        (await thresholds.GetAsync(zoneId, cancellationToken)).ToActionResult(Ok);

    /// <summary>Applies to measurements saved afterwards; existing measurements keep their own copy.</summary>
    [HttpPut("api/zones/{zoneId:guid}/water-thresholds")]
    [RequireCooperativeOwner]
    [ProducesResponseType<ZoneWaterThresholds>(StatusCodes.Status200OK)]
    public async Task<IActionResult> Set(Guid zoneId, SetWaterThresholdsRequest request, CancellationToken cancellationToken) =>
        (await thresholds.SetAsync(zoneId, request, cancellationToken)).ToActionResult(Ok);
}
