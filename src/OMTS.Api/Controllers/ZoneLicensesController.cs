using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using OMTS.Api.Common;
using OMTS.Api.Filters;
using OMTS.Application.Features.Files;
using OMTS.Application.Features.Farming.Licenses;

namespace OMTS.Api.Controllers;

/// <summary>
/// FARM-03 – zone licenses; Cooperative Owner only. Create, edit and replace are multipart forms: the
/// fields of <see cref="SaveZoneLicenseRequest"/> (repeat <c>zoneIds</c> per zone) plus the file in <c>file</c>
/// (PDF, JPEG or PNG, at most 10 MB).
/// </summary>
[ApiController]
[Authorize]
[RequireCooperativeOwner]
public sealed class ZoneLicensesController(ZoneLicenseService licenses) : ControllerBase
{
    private const long MaxRequestBytes = 11 * 1024 * 1024;

    /// <summary>Every license covering the zone with its state as of <c>asOf</c> (default today), plus Missing rows.</summary>
    [HttpGet("api/zones/{zoneId:guid}/licenses")]
    [ProducesResponseType<ZoneLicenseOverviewView>(StatusCodes.Status200OK)]
    public async Task<IActionResult> ListForZone(Guid zoneId, DateOnly? asOf, CancellationToken cancellationToken) =>
        (await licenses.ListForZoneAsync(zoneId, asOf, cancellationToken)).ToActionResult(Ok);

    [HttpGet("api/zone-licenses/{id:guid}")]
    [ProducesResponseType<ZoneLicenseView>(StatusCodes.Status200OK)]
    public async Task<IActionResult> Get(Guid id, DateOnly? asOf, CancellationToken cancellationToken) =>
        (await licenses.GetAsync(id, asOf, cancellationToken)).ToActionResult(Ok);

    [HttpPost("api/zone-licenses")]
    [Consumes("multipart/form-data")]
    [RequestSizeLimit(MaxRequestBytes)]
    [ProducesResponseType<ZoneLicenseView>(StatusCodes.Status201Created)]
    public async Task<IActionResult> Create([FromForm] SaveZoneLicenseRequest request, IFormFile? file, CancellationToken cancellationToken) =>
        (await licenses.CreateAsync(request, await IncomingAsync(file, cancellationToken), cancellationToken))
            .ToActionResult(l => StatusCode(StatusCodes.Status201Created, l));

    /// <summary>Corrects a license that has not been replaced; <c>file</c> is optional here.</summary>
    [HttpPut("api/zone-licenses/{id:guid}")]
    [Consumes("multipart/form-data")]
    [RequestSizeLimit(MaxRequestBytes)]
    [ProducesResponseType<ZoneLicenseView>(StatusCodes.Status200OK)]
    public async Task<IActionResult> Update(Guid id, [FromForm] SaveZoneLicenseRequest request, IFormFile? file, CancellationToken cancellationToken) =>
        (await licenses.UpdateAsync(id, request, await IncomingAsync(file, cancellationToken), cancellationToken)).ToActionResult(Ok);

    /// <summary>Renewal: a new license replacing this one; zones default to the replaced license's zones.</summary>
    [HttpPost("api/zone-licenses/{id:guid}/replace")]
    [Consumes("multipart/form-data")]
    [RequestSizeLimit(MaxRequestBytes)]
    [ProducesResponseType<ZoneLicenseView>(StatusCodes.Status201Created)]
    public async Task<IActionResult> Replace(Guid id, [FromForm] SaveZoneLicenseRequest request, IFormFile? file, CancellationToken cancellationToken) =>
        (await licenses.ReplaceAsync(id, request, await IncomingAsync(file, cancellationToken), cancellationToken))
            .ToActionResult(l => StatusCode(StatusCodes.Status201Created, l));

    private static async Task<IncomingFile?> IncomingAsync(IFormFile? file, CancellationToken cancellationToken) =>
        file is null ? null : await file.ToIncomingFileAsync(cancellationToken);
}
