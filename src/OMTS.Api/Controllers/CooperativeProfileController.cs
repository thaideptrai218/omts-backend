using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using OMTS.Api.Common;
using OMTS.Api.Filters;
using OMTS.Application.Common.Results;
using OMTS.Application.Features.Cooperatives;

namespace OMTS.Api.Controllers;

/// <summary>
/// FARM-01 – the cooperative profile, with its certifications and the preview of the provenance page block; Cooperative
/// Owner only. The public page reads the logo and the cover photo anonymously.
/// </summary>
[ApiController]
public sealed class CooperativeProfileController(CooperativeProfileService profiles, CooperativeCoverPhotoService coverPhotos) : ControllerBase
{
    [HttpGet("api/cooperative/profile")]
    [Authorize]
    [RequireCooperativeOwner]
    [ProducesResponseType<CooperativeProfileView>(StatusCodes.Status200OK)]
    public async Task<IActionResult> Get(CancellationToken cancellationToken) =>
        (await profiles.GetAsync(cancellationToken)).ToActionResult(Ok);

    /// <summary>Validates every field and saves the profile (MSG-GEN-05).</summary>
    [HttpPut("api/cooperative/profile")]
    [Authorize]
    [RequireCooperativeOwner]
    [ProducesResponseType<CooperativeProfileSaved>(StatusCodes.Status200OK)]
    public async Task<IActionResult> Update(UpdateCooperativeProfileRequest request, CancellationToken cancellationToken) =>
        (await profiles.UpdateAsync(request, cancellationToken)).ToActionResult(Ok);

    /// <summary>Multipart field <c>file</c>: PNG, JPEG or WebP, at most 2 MB; replaces the logo at once.</summary>
    [HttpPost("api/cooperative/profile/logo")]
    [Authorize]
    [RequireCooperativeOwner]
    [RequestSizeLimit(3 * 1024 * 1024)]
    [UploadField("Logo")]
    [ProducesResponseType<CooperativeProfileView>(StatusCodes.Status200OK)]
    public async Task<IActionResult> UploadLogo(IFormFile? file, CancellationToken cancellationToken)
    {
        if (file is null)
        {
            return AppError.Invalid("Logo").ToProblem();
        }

        return (await profiles.UploadLogoAsync(await file.ToIncomingFileAsync(cancellationToken), cancellationToken)).ToActionResult(Ok);
    }

    [HttpDelete("api/cooperative/profile/logo")]
    [Authorize]
    [RequireCooperativeOwner]
    [ProducesResponseType<CooperativeProfileView>(StatusCodes.Status200OK)]
    public async Task<IActionResult> RemoveLogo(CancellationToken cancellationToken) =>
        (await profiles.RemoveLogoAsync(cancellationToken)).ToActionResult(Ok);

    /// <summary>
    /// Multipart field <c>file</c>: PNG, JPEG or WebP, at most 5 MB; replaces the cover photo at once and deletes the
    /// replaced file. A storage failure answers 502 MSG-GEN-03 and keeps the current cover photo.
    /// </summary>
    [HttpPost("api/cooperative/profile/cover-photo")]
    [Authorize]
    [RequireCooperativeOwner]
    [RequestSizeLimit(6 * 1024 * 1024)]
    [UploadField("Cover photo")]
    [ProducesResponseType<CooperativeProfileView>(StatusCodes.Status200OK)]
    public async Task<IActionResult> UploadCoverPhoto(IFormFile? file, CancellationToken cancellationToken)
    {
        if (file is null)
        {
            return AppError.Invalid("Cover photo").ToProblem();
        }

        return (await coverPhotos.UploadAsync(await file.ToIncomingFileAsync(cancellationToken), cancellationToken)).ToActionResult(Ok);
    }

    [HttpDelete("api/cooperative/profile/cover-photo")]
    [Authorize]
    [RequireCooperativeOwner]
    [ProducesResponseType<CooperativeProfileView>(StatusCodes.Status200OK)]
    public async Task<IActionResult> RemoveCoverPhoto(CancellationToken cancellationToken) =>
        (await coverPhotos.RemoveAsync(cancellationToken)).ToActionResult(Ok);

    /// <summary>The logo shown on the public provenance page (anonymous).</summary>
    [HttpGet(CooperativePublicFiles.CurrentLogoPath)]
    [AllowAnonymous]
    [ResponseCache(Duration = 300)]
    public async Task<IActionResult> PublicLogo(CancellationToken cancellationToken) =>
        FileOrNotFound(await profiles.OpenLogoAsync(cancellationToken));

    /// <summary>
    /// One logo file, current or replaced (anonymous). This is the URL the profile returns and a published
    /// provenance version stores: it always shows the same image.
    /// </summary>
    [HttpGet(CooperativePublicFiles.LogoFilesPath + "/{year}/{month}/{file}")]
    [AllowAnonymous]
    [ResponseCache(Duration = 86400)]
    public async Task<IActionResult> PublicLogoFile(string year, string month, string file, CancellationToken cancellationToken) =>
        FileOrNotFound(await profiles.OpenLogoFileAsync($"{year}/{month}/{file}", cancellationToken));

    /// <summary>
    /// The current cover photo (anonymous), at the URL the profile returns; a new cover photo gets a new URL, and a
    /// replaced or removed one is 404.
    /// </summary>
    [HttpGet(CooperativePublicFiles.CoverPhotoFilesPath + "/{year}/{month}/{file}")]
    [AllowAnonymous]
    [ResponseCache(Duration = 300)]
    public async Task<IActionResult> PublicCoverPhotoFile(string year, string month, string file, CancellationToken cancellationToken) =>
        FileOrNotFound(await coverPhotos.OpenFileAsync($"{year}/{month}/{file}", cancellationToken));

    private IActionResult FileOrNotFound((Stream Content, string ContentType)? file)
    {
        if (file is not { } found)
        {
            return NotFound();
        }

        Response.Headers.XContentTypeOptions = "nosniff";
        return File(found.Content, found.ContentType);
    }
}
