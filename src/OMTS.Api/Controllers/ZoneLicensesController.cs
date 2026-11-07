using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using OMTS.Api.Common;
using OMTS.Api.Filters;
using OMTS.Application.Features.Files;
using OMTS.Application.Features.Farming.Licenses;

namespace OMTS.Api.Controllers;

/// <summary>
/// FARM-03 – zone licenses; Cooperative Owner only. Create, edit and replace are multipart forms: the
/// fields of <see cref="SaveZoneLicenseRequest"/> (one <c>zoneId</c>) plus the file in <c>file</c>
/// (PDF, JPEG or PNG, at most 10 MB). Writes need the zone In Use (MSG-GEN-09).
/// </summary>
[ApiController]
[Authorize]
[RequireCooperativeOwner]
public sealed class ZoneLicensesController(ZoneLicenseService licenses) : ControllerBase
{
    private const long MaxRequestBytes = 11 * 1024 * 1024;

    /// <summary>The licenses of the zone with their state as of <c>asOf</c> (default today), newest first.</summary>
    [HttpGet("api/zones/{zoneId:guid}/licenses")]
    [ProducesResponseType<ZoneLicenseOverviewView>(StatusCodes.Status200OK)]
    public async Task<IActionResult> ListForZone(Guid zoneId, DateOnly? asOf, CancellationToken cancellationToken) =>
        (await licenses.ListForZoneAsync(zoneId, asOf, cancellationToken)).ToActionResult(Ok);

    [HttpGet("api/zone-licenses/{id:guid}")]
    [ProducesResponseType<ZoneLicenseView>(StatusCodes.Status200OK)]
    public async Task<IActionResult> Get(Guid id, DateOnly? asOf, CancellationToken cancellationToken) =>
        (await licenses.GetAsync(id, asOf, cancellationToken)).ToActionResult(Ok);

    /// <summary>Uploads a license: the form fields below plus its document in <c>file</c> (required).</summary>
    /// <param name="request">The license fields, one form field each.</param>
    /// <param name="file">The license document: PDF, JPEG or PNG, at most 10 MB.</param>
    /// <param name="cancellationToken">Request aborted.</param>
    [HttpPost("api/zone-licenses")]
    [Consumes("multipart/form-data")]
    [RequestSizeLimit(MaxRequestBytes)]
    [UploadField("File")]
    [ProducesResponseType<ZoneLicenseView>(StatusCodes.Status201Created)]
    public async Task<IActionResult> Create([FromForm] SaveZoneLicenseRequest request, IFormFile? file, CancellationToken cancellationToken) =>
        (await licenses.CreateAsync(request, await IncomingAsync(file, cancellationToken), cancellationToken))
            .ToActionResult(l => StatusCode(StatusCodes.Status201Created, l));

    /// <summary>
    /// Corrects a license that is not Replaced (else MSG-GEN-09); <c>rowVersion</c> is required and <c>file</c> is optional
    /// here (a new file replaces the stored one). The zone cannot change.
    /// </summary>
    [HttpPut("api/zone-licenses/{id:guid}")]
    [Consumes("multipart/form-data")]
    [RequestSizeLimit(MaxRequestBytes)]
    [UploadField("File")]
    [ProducesResponseType<ZoneLicenseView>(StatusCodes.Status200OK)]
    public async Task<IActionResult> Update(Guid id, [FromForm] SaveZoneLicenseRequest request, IFormFile? file, CancellationToken cancellationToken) =>
        (await licenses.UpdateAsync(id, request, await IncomingAsync(file, cancellationToken), cancellationToken)).ToActionResult(Ok);

    /// <summary>
    /// Renewal: a new license of the same zone replacing this one, which is Replaced from today, with its own document in
    /// <c>file</c> (required). An empty <c>documentType</c> keeps the replaced one's; no <c>rowVersion</c>.
    /// </summary>
    /// <param name="id">The license being replaced.</param>
    /// <param name="request">The new license's fields, one form field each.</param>
    /// <param name="file">The new license document: PDF, JPEG or PNG, at most 10 MB.</param>
    /// <param name="cancellationToken">Request aborted.</param>
    [HttpPost("api/zone-licenses/{id:guid}/replace")]
    [Consumes("multipart/form-data")]
    [RequestSizeLimit(MaxRequestBytes)]
    [UploadField("File")]
    [ProducesResponseType<ZoneLicenseView>(StatusCodes.Status201Created)]
    public async Task<IActionResult> Replace(Guid id, [FromForm] SaveZoneLicenseRequest request, IFormFile? file, CancellationToken cancellationToken) =>
        (await licenses.ReplaceAsync(id, request, await IncomingAsync(file, cancellationToken), cancellationToken))
            .ToActionResult(l => StatusCode(StatusCodes.Status201Created, l));

    private static async Task<IncomingFile?> IncomingAsync(IFormFile? file, CancellationToken cancellationToken) =>
        file is null ? null : await file.ToIncomingFileAsync(cancellationToken);
}
