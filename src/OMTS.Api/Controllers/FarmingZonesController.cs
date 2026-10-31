using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using OMTS.Api.Common;
using OMTS.Api.Filters;
using OMTS.Application.Features.AccessManagement.Dtos;
using OMTS.Application.Features.Farming.Zones;

namespace OMTS.Api.Controllers;

/// <summary>
/// FARM-02 – farming zones. Writes are Cooperative Owner only; reads return only the zones the caller holds
/// an effective grant in (the Owner sees all).
/// </summary>
[ApiController]
[Route("api/zones")]
[Authorize]
public sealed class FarmingZonesController(FarmingZoneService zones) : ControllerBase
{
    /// <summary>Search by code or name; the license badge is returned to the Owner only.</summary>
    [HttpGet]
    [ProducesResponseType<Paged<FarmingZoneListItem>>(StatusCodes.Status200OK)]
    public async Task<IActionResult> Search(string? search, int? statusId, int page = 1, int pageSize = 20, CancellationToken cancellationToken = default) =>
        Ok(await zones.SearchAsync(search, statusId, page, pageSize, cancellationToken));

    /// <summary>Zone picker for other modules.</summary>
    [HttpGet("lookup")]
    [ProducesResponseType<IReadOnlyList<ZoneLookupItem>>(StatusCodes.Status200OK)]
    public async Task<IActionResult> Lookup(bool includeDiscontinued = false, CancellationToken cancellationToken = default) =>
        Ok(await zones.LookupAsync(includeDiscontinued, cancellationToken));

    /// <summary>Zones and their In Use rafts with coordinates, for the Google Maps view.</summary>
    [HttpGet("map")]
    [ProducesResponseType<IReadOnlyList<MapZone>>(StatusCodes.Status200OK)]
    public async Task<IActionResult> Map(bool includeDiscontinued = false, CancellationToken cancellationToken = default) =>
        Ok(await zones.MapAsync(includeDiscontinued, cancellationToken));

    [HttpGet("{zoneId:guid}")]
    [ProducesResponseType<FarmingZoneView>(StatusCodes.Status200OK)]
    public async Task<IActionResult> Get(Guid zoneId, CancellationToken cancellationToken) =>
        (await zones.GetAsync(zoneId, cancellationToken)).ToActionResult(Ok);

    [HttpPost]
    [RequireCooperativeOwner]
    [ProducesResponseType<FarmingZoneView>(StatusCodes.Status201Created)]
    public async Task<IActionResult> Create(SaveFarmingZoneRequest request, CancellationToken cancellationToken) =>
        (await zones.CreateAsync(request, cancellationToken)).ToActionResult(z => StatusCode(StatusCodes.Status201Created, z));

    [HttpPut("{zoneId:guid}")]
    [RequireCooperativeOwner]
    [ProducesResponseType<FarmingZoneView>(StatusCodes.Status200OK)]
    public async Task<IActionResult> Update(Guid zoneId, SaveFarmingZoneRequest request, CancellationToken cancellationToken) =>
        (await zones.UpdateAsync(zoneId, request, cancellationToken)).ToActionResult(Ok);

    /// <summary>Blocked with MSG-GEN-06 while In Use rafts remain in the zone.</summary>
    [HttpPost("{zoneId:guid}/discontinue")]
    [RequireCooperativeOwner]
    [ProducesResponseType<FarmingZoneView>(StatusCodes.Status200OK)]
    public async Task<IActionResult> Discontinue(Guid zoneId, ChangeStatusRequest request, CancellationToken cancellationToken) =>
        (await zones.DiscontinueAsync(zoneId, request, cancellationToken)).ToActionResult(Ok);

    [HttpPost("{zoneId:guid}/reactivate")]
    [RequireCooperativeOwner]
    [ProducesResponseType<FarmingZoneView>(StatusCodes.Status200OK)]
    public async Task<IActionResult> Reactivate(Guid zoneId, ChangeStatusRequest request, CancellationToken cancellationToken) =>
        (await zones.ReactivateAsync(zoneId, request, cancellationToken)).ToActionResult(Ok);
}
