using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using OMTS.Application.Common.Interfaces.Storage;
using OMTS.Application.Features.Files;
using OMTS.Infrastructure.Storage;

namespace OMTS.Api.Controllers;

/// <summary>
/// Serves files of the Local storage provider through the signed, expiring URLs that
/// <see cref="IFileStorage.GetReadUrlAsync"/> hands out (the local equivalent of an Azure SAS link).
/// The signature is the permission: it is only issued after the owning record was checked.
/// </summary>
[ApiController]
[Route("api/files/local")]
[AllowAnonymous]
[ApiExplorerSettings(IgnoreApi = true)]
public sealed class LocalFilesController(
    IOptions<StorageOptions> options,
    IServiceProvider services,
    IFileStorage storage,
    TimeProvider timeProvider) : ControllerBase
{
    [HttpGet("{**key}")]
    public async Task<IActionResult> Download(string key, long expires, string? sig, CancellationToken cancellationToken)
    {
        if (options.Value.Provider != StorageOptions.LocalProvider
            || FileValidator.ContentTypeOfKey(key) is not { } contentType
            || !services.GetRequiredService<LocalFileUrlSigner>().IsValid(key, expires, sig, timeProvider.GetUtcNow()))
        {
            return NotFound();
        }

        var content = await storage.OpenReadAsync(key, cancellationToken);
        if (content is null)
        {
            return NotFound();
        }

        // Stored files are user uploads served from the API origin: never let a browser run them.
        Response.Headers.XContentTypeOptions = "nosniff";
        Response.Headers.ContentSecurityPolicy = "sandbox; default-src 'none'";
        return File(content, contentType);
    }
}
