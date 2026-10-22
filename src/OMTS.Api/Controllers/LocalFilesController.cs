using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.StaticFiles;
using Microsoft.Extensions.Options;
using OMTS.Application.Common.Interfaces.Storage;
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
    LocalFileUrlSigner signer,
    IFileStorage storage,
    TimeProvider timeProvider) : ControllerBase
{
    private static readonly FileExtensionContentTypeProvider ContentTypes = new();

    [HttpGet("{**key}")]
    public async Task<IActionResult> Download(string key, long expires, string? sig, CancellationToken cancellationToken)
    {
        if (options.Value.Provider != StorageOptions.LocalProvider || !signer.IsValid(key, expires, sig, timeProvider.GetUtcNow()))
        {
            return NotFound();
        }

        var content = await storage.OpenReadAsync(key, cancellationToken);
        if (content is null)
        {
            return NotFound();
        }

        var contentType = ContentTypes.TryGetContentType(key, out var type) ? type : "application/octet-stream";
        return File(content, contentType);
    }
}
