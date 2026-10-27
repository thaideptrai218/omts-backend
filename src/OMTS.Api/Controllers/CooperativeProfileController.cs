using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using OMTS.Api.Common;
using OMTS.Api.Filters;
using OMTS.Application.Features.Cooperatives;

namespace OMTS.Api.Controllers;

/// <summary>FARM-01 – the cooperative profile; Cooperative Owner only. The public page reads the logo anonymously.</summary>
[ApiController]
public sealed class CooperativeProfileController(CooperativeProfileService profiles) : ControllerBase
{
    [HttpGet("api/cooperative/profile")]
    [Authorize]
    [RequireCooperativeOwner]
    [ProducesResponseType<CooperativeProfileView>(StatusCodes.Status200OK)]
    public async Task<IActionResult> Get(CancellationToken cancellationToken) =>
        (await profiles.GetAsync(cancellationToken)).ToActionResult(Ok);

    /// <summary>Validates every field; the first complete save moves Preliminary → Completed (MSG10).</summary>
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
            return Application.Common.Results.AppError.Invalid("Logo").ToProblem();
        }

        return (await profiles.UploadLogoAsync(await file.ToIncomingFileAsync(cancellationToken), cancellationToken)).ToActionResult(Ok);
    }

    [HttpDelete("api/cooperative/profile/logo")]
    [Authorize]
    [RequireCooperativeOwner]
    [ProducesResponseType<CooperativeProfileView>(StatusCodes.Status200OK)]
    public async Task<IActionResult> RemoveLogo(CancellationToken cancellationToken) =>
        (await profiles.RemoveLogoAsync(cancellationToken)).ToActionResult(Ok);

    /// <summary>The logo shown on the public provenance page (anonymous).</summary>
    [HttpGet(CooperativeProfileService.PublicLogoPath)]
    [AllowAnonymous]
    [ResponseCache(Duration = 300)]
    public async Task<IActionResult> PublicLogo(CancellationToken cancellationToken)
    {
        if (await profiles.OpenLogoAsync(cancellationToken) is not { } logo)
        {
            return NotFound();
        }

        Response.Headers.XContentTypeOptions = "nosniff";
        return File(logo.Content, logo.ContentType);
    }

    /// <summary>
    /// One logo file, current or replaced (anonymous). This is the URL the profile returns and a published
    /// provenance version stores: it always shows the same image.
    /// </summary>
    [HttpGet(CooperativeProfileService.PublicLogoFilesPath + "/{year}/{month}/{file}")]
    [AllowAnonymous]
    [ResponseCache(Duration = 86400)]
    public async Task<IActionResult> PublicLogoFile(string year, string month, string file, CancellationToken cancellationToken)
    {
        if (await profiles.OpenLogoFileAsync($"{year}/{month}/{file}", cancellationToken) is not { } logo)
        {
            return NotFound();
        }

        Response.Headers.XContentTypeOptions = "nosniff";
        return File(logo.Content, logo.ContentType);
    }
}
