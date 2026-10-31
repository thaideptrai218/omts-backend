using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using OMTS.Api.Common;
using OMTS.Api.Filters;
using OMTS.Application.Features.Cooperatives;
using OMTS.Application.Features.Files;

namespace OMTS.Api.Controllers;

/// <summary>
/// FARM-01.1 – certifications of the cooperative; Cooperative Owner only. Add and edit are multipart forms: the fields
/// of <see cref="SaveCooperativeCertificationRequest"/> plus the certificate in <c>file</c> (PDF, JPEG or PNG, at most
/// 10 MB). The state (Valid, ExpiringSoon, Expired) is worked out from the expiry date as of today.
/// </summary>
[ApiController]
[Authorize]
[RequireCooperativeOwner]
[Route("api/cooperative/certifications")]
public sealed class CooperativeCertificationsController(CooperativeCertificationService certifications) : ControllerBase
{
    private const long MaxRequestBytes = 11 * 1024 * 1024;

    /// <summary>Every certification with its state, by name.</summary>
    [HttpGet]
    [ProducesResponseType<IReadOnlyList<CooperativeCertificationView>>(StatusCodes.Status200OK)]
    public async Task<IActionResult> List(CancellationToken cancellationToken) =>
        (await certifications.ListAsync(cancellationToken)).ToActionResult(Ok);

    /// <summary>Adds a certification: the form fields below plus its certificate in <c>file</c> (required).</summary>
    /// <param name="request">The certification fields, one form field each.</param>
    /// <param name="file">The certificate: PDF, JPEG or PNG, at most 10 MB.</param>
    /// <param name="cancellationToken">Request aborted.</param>
    [HttpPost]
    [Consumes("multipart/form-data")]
    [RequestSizeLimit(MaxRequestBytes)]
    [UploadField("File")]
    [ProducesResponseType<CooperativeCertificationSaved>(StatusCodes.Status201Created)]
    public async Task<IActionResult> Create([FromForm] SaveCooperativeCertificationRequest request, IFormFile? file, CancellationToken cancellationToken) =>
        (await certifications.CreateAsync(request, await IncomingAsync(file, cancellationToken), cancellationToken))
            .ToActionResult(saved => StatusCode(StatusCodes.Status201Created, saved));

    /// <summary>
    /// Corrects a certification; <c>rowVersion</c> is required and <c>file</c> is optional here (a new file replaces
    /// the stored one).
    /// </summary>
    /// <param name="id">The certification.</param>
    /// <param name="request">The certification fields, one form field each.</param>
    /// <param name="file">A new certificate: PDF, JPEG or PNG, at most 10 MB.</param>
    /// <param name="cancellationToken">Request aborted.</param>
    [HttpPut("{id:guid}")]
    [Consumes("multipart/form-data")]
    [RequestSizeLimit(MaxRequestBytes)]
    [UploadField("File")]
    [ProducesResponseType<CooperativeCertificationSaved>(StatusCodes.Status200OK)]
    public async Task<IActionResult> Update(Guid id, [FromForm] SaveCooperativeCertificationRequest request, IFormFile? file, CancellationToken cancellationToken) =>
        (await certifications.UpdateAsync(id, request, await IncomingAsync(file, cancellationToken), cancellationToken)).ToActionResult(Ok);

    /// <summary>Removes the certification and its file (FARM-01.1, MSG-GEN-05); the audit log keeps its values.</summary>
    /// <param name="id">The certification.</param>
    /// <param name="rowVersion">The <c>rowVersion</c> the certification was read with; an older one answers MSG06.</param>
    /// <param name="cancellationToken">Request aborted.</param>
    [HttpDelete("{id:guid}")]
    [ProducesResponseType<CooperativeCertificationRemoved>(StatusCodes.Status200OK)]
    public async Task<IActionResult> Remove(Guid id, [FromQuery] string? rowVersion, CancellationToken cancellationToken) =>
        (await certifications.RemoveAsync(id, rowVersion, cancellationToken)).ToActionResult(Ok);

    private static async Task<IncomingFile?> IncomingAsync(IFormFile? file, CancellationToken cancellationToken) =>
        file is null ? null : await file.ToIncomingFileAsync(cancellationToken);
}
