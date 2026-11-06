using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using OMTS.Api.Filters;
using OMTS.Application.Features.Authorization;
using OMTS.Application.Features.Authorization.Models;
using OMTS.Application.Features.WaterQuality;
using OMTS.Application.Features.WaterQuality.Dtos;

namespace OMTS.Api.Controllers;

/// <summary>WATER-03 step 1. Cooperative Owner: every zone; a Zone Manager: the zones of their pairs; other roles: 403 MSG-GEN-08 (§I.4.4).</summary>
[ApiController]
[Route("api/water-measurements/zones")]
[Authorize]
public sealed class WaterZoneOverviewController(WaterZoneOverviewService overview) : ControllerBase
{
    /// <summary>The zones with their latest measurement time and water alert, ordered by code; <c>inUseOnly</c> for the WATER-01 zone picker.</summary>
    [HttpGet]
    [RequirePermission(PermissionFunctions.WaterMeasurements, PermissionAction.View, PermissionScope.AnyZone)]
    [ProducesResponseType<IReadOnlyList<WaterZoneOverviewItem>>(StatusCodes.Status200OK)]
    public async Task<IActionResult> List(bool inUseOnly = false, CancellationToken cancellationToken = default) =>
        Ok(await overview.ListAsync(inUseOnly, cancellationToken));
}
