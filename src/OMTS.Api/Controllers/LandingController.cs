using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using OMTS.Api.Common;
using OMTS.Api.Extensions;
using OMTS.Api.Filters;
using OMTS.Application.Common.Results;
using OMTS.Application.Features.Cooperatives.Landing;
using OMTS.Domain.Enums;

namespace OMTS.Api.Controllers;

/// <summary>The public introduction page: anyone reads it; the Cooperative Owner chooses its pictures and its example invoice.</summary>
[ApiController]
public sealed class LandingController(LandingService landing) : ControllerBase
{
    [HttpGet("api/public/landing")]
    [AllowAnonymous]
    [EnableRateLimiting(RateLimitingExtensions.ProvenancePages)]
    [ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
    [ProducesResponseType<LandingPublicView>(StatusCodes.Status200OK)]
    public async Task<IActionResult> Public(CancellationToken cancellationToken) =>
        (await landing.PublicAsync(cancellationToken)).ToActionResult(Ok);

    [HttpGet("api/cooperative/landing")]
    [Authorize]
    [RequireCooperativeOwner]
    [ProducesResponseType<LandingSettingsView>(StatusCodes.Status200OK)]
    public async Task<IActionResult> Settings(CancellationToken cancellationToken) =>
        (await landing.SettingsAsync(cancellationToken)).ToActionResult(Ok);

    /// <summary>Multipart: <c>file</c> (PNG, JPEG or WebP, at most 5 MB), <c>section</c> (Background, Press, Gallery) and an optional <c>caption</c>.</summary>
    [HttpPost("api/cooperative/landing/photos")]
    [Authorize]
    [RequireCooperativeOwner]
    [Consumes("multipart/form-data")]
    [RequestSizeLimit(6 * 1024 * 1024)]
    [UploadField("File")]
    [ProducesResponseType<LandingSettingsView>(StatusCodes.Status200OK)]
    public async Task<IActionResult> AddPhoto([FromForm] LandingSection section, [FromForm] string? caption, IFormFile? file, CancellationToken cancellationToken)
    {
        if (file is null)
        {
            return AppError.Invalid("File").ToProblem();
        }

        return (await landing.AddPhotoAsync(section, caption, await file.ToIncomingFileAsync(cancellationToken), cancellationToken)).ToActionResult(Ok);
    }

    [HttpDelete("api/cooperative/landing/photos/{id:guid}")]
    [Authorize]
    [RequireCooperativeOwner]
    [ProducesResponseType<LandingSettingsView>(StatusCodes.Status200OK)]
    public async Task<IActionResult> RemovePhoto(Guid id, CancellationToken cancellationToken) =>
        (await landing.RemovePhotoAsync(id, cancellationToken)).ToActionResult(Ok);

    [HttpPut("api/cooperative/landing/example-invoice")]
    [Authorize]
    [RequireCooperativeOwner]
    [ProducesResponseType<LandingSettingsView>(StatusCodes.Status200OK)]
    public async Task<IActionResult> SetExample(SetFeaturedInvoiceRequest request, CancellationToken cancellationToken) =>
        (await landing.SetFeaturedAsync(request.InvoiceCode, cancellationToken)).ToActionResult(Ok);
}
