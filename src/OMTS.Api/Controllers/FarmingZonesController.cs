using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using OMTS.Api.Common;
using OMTS.Api.Extensions;
using OMTS.Api.Filters;
using OMTS.Application.Features.AccessManagement.Dtos;
using OMTS.Application.Features.Farming.Zones;
using OMTS.Application.Features.Farming.Zones.BoundaryScan;
using OMTS.Application.Features.Scans;

namespace OMTS.Api.Controllers;

/// <summary>
/// FARM-02 – farming zones. Writes are Cooperative Owner only; reads return only the zones the caller holds
/// an effective grant in (the Owner sees all).
/// </summary>
[ApiController]
[Route("api/zones")]
[Authorize]
public sealed class FarmingZonesController(FarmingZoneService zones, ZoneBoundaryScanService boundaryScans) : ControllerBase
{
    // A 10 MB photo plus the multipart envelope; a larger photo still reaches the validator's MSG-GEN-01.
    private const long MaxScanUploadBytes = 12 * 1024 * 1024;

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

    /// <summary>The proposed code of a new zone (KV01, KV02…; GBR-03); the Owner may type another one.</summary>
    [HttpGet("next-code")]
    [RequireCooperativeOwner]
    [ProducesResponseType<FarmingZoneCodeProposal>(StatusCodes.Status200OK)]
    public async Task<IActionResult> NextCode(CancellationToken cancellationToken) => Ok(await zones.ProposeCodeAsync(cancellationToken));

    [HttpGet("{zoneId:guid}")]
    [ProducesResponseType<FarmingZoneView>(StatusCodes.Status200OK)]
    public async Task<IActionResult> Get(Guid zoneId, CancellationToken cancellationToken) =>
        (await zones.GetAsync(zoneId, cancellationToken)).ToActionResult(Ok);

    [HttpPost]
    [RequireCooperativeOwner]
    [ProducesResponseType<FarmingZoneSaved>(StatusCodes.Status201Created)]
    public async Task<IActionResult> Create(SaveFarmingZoneRequest request, CancellationToken cancellationToken) =>
        (await zones.CreateAsync(request, cancellationToken)).ToActionResult(z => StatusCode(StatusCodes.Status201Created, z));

    /// <summary>Edits an In Use zone; a Discontinued zone answers MSG-GEN-09 (resume it first).</summary>
    [HttpPut("{zoneId:guid}")]
    [RequireCooperativeOwner]
    [ProducesResponseType<FarmingZoneSaved>(StatusCodes.Status200OK)]
    public async Task<IActionResult> Update(Guid zoneId, SaveFarmingZoneRequest request, CancellationToken cancellationToken) =>
        (await zones.UpdateAsync(zoneId, request, cancellationToken)).ToActionResult(Ok);

    /// <summary>
    /// Saves the licensed sea area of an In Use zone as GeoJSON, or clears it with a null <c>boundary</c>. In Use rafts
    /// pinned outside it are named in the warning <c>RAFTS_OUTSIDE_ZONE_BOUNDARY</c>; the save goes through.
    /// </summary>
    [HttpPut("{zoneId:guid}/boundary")]
    [RequireCooperativeOwner]
    [ProducesResponseType<FarmingZoneSaved>(StatusCodes.Status200OK)]
    public async Task<IActionResult> SetBoundary(Guid zoneId, SaveZoneBoundaryRequest request, CancellationToken cancellationToken) =>
        (await zones.SetBoundaryAsync(zoneId, request, cancellationToken)).ToActionResult(Ok);

    /// <summary>
    /// Starts reading the corner tables of a photographed sea-area licence map (multipart <c>photo</c>, JPEG/PNG, at most 10 MB).
    /// 202 with the <c>scanId</c> to poll; the zone must be In Use (409 otherwise). A full queue is 503 OCR_BUSY, more than 20 scans
    /// an hour per account is 429. Nothing is saved.
    /// </summary>
    [HttpPost("{zoneId:guid}/boundary-scans")]
    [RequireCooperativeOwner]
    [EnableRateLimiting(RateLimitingExtensions.ZoneMapScans)]
    [RequestSizeLimit(MaxScanUploadBytes)]
    [UploadField(PhotoScanGateway.PhotoField)]
    [Consumes("multipart/form-data")]
    [ProducesResponseType<ScanStarted>(StatusCodes.Status202Accepted)]
    public async Task<IActionResult> StartBoundaryScan(Guid zoneId, IFormFile? photo, CancellationToken cancellationToken)
    {
        var file = photo is null ? null : await photo.ToIncomingFileAsync(cancellationToken);
        return (await boundaryScans.StartAsync(zoneId, file, cancellationToken)).ToActionResult(started => Accepted(started));
    }

    /// <summary>
    /// The scan: <c>status</c> Pending, Done or Failed (<c>errorCode</c> UNAVAILABLE, TIMEOUT, BAD_RESPONSE or NOT_CONFIGURED) and,
    /// when Done, the <c>reading</c> with the VN-2000 corners per zone and the area checks. Kept 15 minutes; only the account that
    /// started it can read it, anyone else gets 404.
    /// </summary>
    [HttpGet("{zoneId:guid}/boundary-scans/{scanId:guid}")]
    [RequireCooperativeOwner]
    [ProducesResponseType<ScanStatusView<ZoneMapReading>>(StatusCodes.Status200OK)]
    public IActionResult GetBoundaryScan(Guid zoneId, Guid scanId) => boundaryScans.Get(zoneId, scanId).ToActionResult(Ok);

    /// <summary>
    /// Like the zone scan, before the zone exists (the create-zone form): same photo, queue and limits (the 20 an hour per account
    /// are shared with the zone scans). The scan belongs to no zone: it is found on <c>GET api/zones/boundary-scans/{scanId}</c> only.
    /// </summary>
    [HttpPost("boundary-scans")]
    [RequireCooperativeOwner]
    [EnableRateLimiting(RateLimitingExtensions.ZoneMapScans)]
    [RequestSizeLimit(MaxScanUploadBytes)]
    [UploadField(PhotoScanGateway.PhotoField)]
    [Consumes("multipart/form-data")]
    [ProducesResponseType<ScanStarted>(StatusCodes.Status202Accepted)]
    public async Task<IActionResult> StartZoneLessBoundaryScan(IFormFile? photo, CancellationToken cancellationToken)
    {
        var file = photo is null ? null : await photo.ToIncomingFileAsync(cancellationToken);
        return boundaryScans.StartWithoutZone(file).ToActionResult(started => Accepted(started));
    }

    /// <summary>The zone-less scan, shaped like the zone scan; 404 for another account, a zone scan or after 15 minutes.</summary>
    [HttpGet("boundary-scans/{scanId:guid}")]
    [RequireCooperativeOwner]
    [ProducesResponseType<ScanStatusView<ZoneMapReading>>(StatusCodes.Status200OK)]
    public IActionResult GetZoneLessBoundaryScan(Guid scanId) => boundaryScans.GetWithoutZone(scanId).ToActionResult(Ok);

    /// <summary>Blocked with MSG-GEN-06 while In Use rafts remain in the zone.</summary>
    [HttpPost("{zoneId:guid}/discontinue")]
    [RequireCooperativeOwner]
    [ProducesResponseType<FarmingZoneView>(StatusCodes.Status200OK)]
    public async Task<IActionResult> Discontinue(Guid zoneId, ChangeStatusRequest request, CancellationToken cancellationToken) =>
        (await zones.DiscontinueAsync(zoneId, request, cancellationToken)).ToActionResult(Ok);

    /// <summary>Resume (FARM-02.3).</summary>
    [HttpPost("{zoneId:guid}/reactivate")]
    [RequireCooperativeOwner]
    [ProducesResponseType<FarmingZoneView>(StatusCodes.Status200OK)]
    public async Task<IActionResult> Reactivate(Guid zoneId, ChangeStatusRequest request, CancellationToken cancellationToken) =>
        (await zones.ReactivateAsync(zoneId, request, cancellationToken)).ToActionResult(Ok);
}
