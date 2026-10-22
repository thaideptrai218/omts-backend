using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using OMTS.Application.Common.Interfaces.Storage;
using OMTS.Application.Features.Files;

namespace OMTS.Infrastructure.Storage;

/// <summary>
/// Stores files in a folder (<c>Storage:Provider = Local</c>), for simple development. Read URLs are
/// signed, expiring links served by the API's file endpoint.
/// </summary>
internal sealed class LocalFileStorage(
    IOptions<StorageOptions> options,
    IHostEnvironment environment,
    LocalFileUrlSigner signer,
    TimeProvider timeProvider,
    ILogger<LocalFileStorage> logger) : IFileStorage
{
    private static readonly TimeSpan DefaultLifetime = TimeSpan.FromMinutes(10);

    private string Root => Path.TrimEndingDirectorySeparator(
        Path.GetFullPath(Path.Combine(environment.ContentRootPath, options.Value.Local.RootPath)));

    public async Task<FileUploadResult> UploadAsync(ValidatedFile file, string category, CancellationToken cancellationToken = default)
    {
        var key = StorageKeys.Create(category, file.Format, timeProvider.GetUtcNow());
        try
        {
            var path = PathOf(key);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            await using var target = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
            await file.File.Content.CopyToAsync(target, cancellationToken);
            return FileUploadResult.Stored(key);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            logger.LogError(exception, "Local storage upload of {Key} failed", key);
            return FileUploadResult.Failed("STORAGE_ERROR");
        }
    }

    public Task<Stream?> OpenReadAsync(string key, CancellationToken cancellationToken = default)
    {
        var path = StorageKeys.IsValid(key) ? PathOf(key) : null;
        Stream? stream = path is not null && File.Exists(path) ? File.OpenRead(path) : null;
        return Task.FromResult(stream);
    }

    public Task<string> GetReadUrlAsync(string key, TimeSpan? lifetime = null, CancellationToken cancellationToken = default) =>
        Task.FromResult(signer.CreateUrl(key, timeProvider.GetUtcNow() + (lifetime ?? DefaultLifetime)));

    public Task<bool> DeleteAsync(string key, CancellationToken cancellationToken = default)
    {
        if (!StorageKeys.IsValid(key))
        {
            return Task.FromResult(false);
        }

        try
        {
            var path = PathOf(key);
            if (!File.Exists(path))
            {
                return Task.FromResult(false);
            }

            File.Delete(path);
            return Task.FromResult(true);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            logger.LogError(exception, "Local storage delete of {Key} failed", key);
            return Task.FromResult(false);
        }
    }

    private string PathOf(string key)
    {
        var root = Root;
        var path = Path.GetFullPath(Path.Combine(root, key));
        // Keys are validated, but never let a path leave the storage folder.
        return path.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.Ordinal)
            ? path
            : throw new ArgumentException("Invalid storage key.", nameof(key));
    }
}
