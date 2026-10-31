using OMTS.Application.Common.Models.Storage;
using OMTS.Application.Features.Files.Models;
using Azure;
using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;
using Azure.Storage.Sas;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using OMTS.Application.Common.Exceptions;
using OMTS.Application.Common.Interfaces.Storage;
using OMTS.Application.Features.Files;

namespace OMTS.Infrastructure.Storage;

/// <summary>
/// Azure Blob Storage (<c>Storage:Provider = AzureBlob</c>): Azurite in development
/// (<c>UseDevelopmentStorage=true</c>), Azure Blob in production. The container is private; read URLs
/// are read-only SAS links.
/// </summary>
internal sealed class AzureBlobFileStorage(
    IOptions<StorageOptions> options,
    TimeProvider timeProvider,
    ILogger<AzureBlobFileStorage> logger) : IFileStorage
{
    private static readonly TimeSpan DefaultLifetime = TimeSpan.FromMinutes(10);

    private BlobContainerClient? _container;
    private bool _containerChecked;

    // Created on first use and not cached on failure, so a fixed connection string works without a restart.
    private BlobContainerClient Container =>
        _container ??= new BlobContainerClient(options.Value.AzureBlob.ConnectionString, options.Value.AzureBlob.Container);

    public async Task<FileUploadResult> UploadAsync(ValidatedFile file, string category, CancellationToken cancellationToken = default)
    {
        var key = StorageKeys.Create(category, file.Format, timeProvider.GetUtcNow());
        try
        {
            await EnsureContainerAsync(cancellationToken);
            await Container.GetBlobClient(key).UploadAsync(
                file.File.Content,
                new BlobUploadOptions { HttpHeaders = new BlobHttpHeaders { ContentType = file.ContentType } },
                cancellationToken);
            return FileUploadResult.Stored(key);
        }
        catch (Exception exception) when (!cancellationToken.IsCancellationRequested)
        {
            // Network errors surface as RequestFailedException, AggregateException (after retries),
            // HttpRequestException or a timeout; all are a failed upload for the caller (MSG-GEN-03).
            logger.LogError(exception, "Blob upload of {Key} failed", key);
            return FileUploadResult.Failed("STORAGE_ERROR");
        }
    }

    public async Task<Stream?> OpenReadAsync(string key, CancellationToken cancellationToken = default)
    {
        try
        {
            var download = await Container.GetBlobClient(key).DownloadStreamingAsync(cancellationToken: cancellationToken);
            return download.Value.Content;
        }
        catch (RequestFailedException exception) when (exception.Status == 404)
        {
            return null;
        }
        catch (Exception exception) when (!cancellationToken.IsCancellationRequested)
        {
            logger.LogError(exception, "Blob read of {Key} failed", key);
            throw new FileStorageUnavailableException(key, exception);
        }
    }

    public Task<string> GetReadUrlAsync(string key, TimeSpan? lifetime = null, CancellationToken cancellationToken = default)
    {
        var blob = Container.GetBlobClient(key);
        var sas = new BlobSasBuilder(BlobSasPermissions.Read, timeProvider.GetUtcNow() + (lifetime ?? DefaultLifetime))
        {
            BlobContainerName = blob.BlobContainerName,
            BlobName = blob.Name,
            // Azurite serves plain HTTP; real accounts get HTTPS-only links.
            Protocol = blob.Uri.Scheme == Uri.UriSchemeHttps ? SasProtocol.Https : SasProtocol.HttpsAndHttp,
        };
        try
        {
            return Task.FromResult(blob.GenerateSasUri(sas).ToString());
        }
        catch (Exception exception) when (exception is InvalidOperationException or ArgumentException)
        {
            // A connection string without an account key cannot sign SAS links.
            logger.LogError(exception, "Read URL for {Key} could not be signed", key);
            throw new FileStorageUnavailableException(key, exception);
        }
    }

    public async Task<bool> DeleteAsync(string key, CancellationToken cancellationToken = default)
    {
        try
        {
            var response = await Container.GetBlobClient(key).DeleteIfExistsAsync(cancellationToken: cancellationToken);
            return response.Value;
        }
        catch (Exception exception) when (!cancellationToken.IsCancellationRequested)
        {
            logger.LogError(exception, "Blob delete of {Key} failed", key);
            return false;
        }
    }

    public async Task<IReadOnlyList<StoredFile>> ListAsync(string category, CancellationToken cancellationToken = default)
    {
        var prefix = StorageKeys.ListPrefix(category);
        var files = new List<StoredFile>();
        try
        {
            await foreach (var blob in Container.GetBlobsAsync(prefix: prefix, cancellationToken: cancellationToken))
            {
                // Blobs this provider did not write (no storage key shape) are never listed, so never cleaned up.
                if (StorageKeys.IsValid(blob.Name) && blob.Properties.LastModified is { } lastModified)
                {
                    files.Add(new StoredFile(blob.Name, lastModified.ToUniversalTime()));
                }
            }

            return files;
        }
        catch (RequestFailedException exception) when (exception.Status == 404)
        {
            // No container yet: nothing was ever uploaded.
            return [];
        }
        catch (Exception exception) when (!cancellationToken.IsCancellationRequested)
        {
            logger.LogError(exception, "Blob listing of {Category} failed", category);
            throw new FileStorageUnavailableException(prefix, exception);
        }
    }

    private async Task EnsureContainerAsync(CancellationToken cancellationToken)
    {
        if (_containerChecked)
        {
            return;
        }

        await Container.CreateIfNotExistsAsync(PublicAccessType.None, cancellationToken: cancellationToken);
        _containerChecked = true;
    }
}
