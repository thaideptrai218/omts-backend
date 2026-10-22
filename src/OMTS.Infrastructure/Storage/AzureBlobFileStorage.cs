using Azure;
using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;
using Azure.Storage.Sas;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using OMTS.Application.Common.Interfaces.Storage;

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

    private readonly Lazy<BlobContainerClient> _container = new(() =>
        new BlobContainerClient(options.Value.AzureBlob.ConnectionString, options.Value.AzureBlob.Container));

    private bool _containerChecked;

    public async Task<FileUploadResult> UploadAsync(
        Stream content, string fileName, string contentType, string category, CancellationToken cancellationToken = default)
    {
        var key = StorageKeys.Create(category, fileName, timeProvider.GetUtcNow());
        try
        {
            await EnsureContainerAsync(cancellationToken);
            await _container.Value.GetBlobClient(key).UploadAsync(
                content,
                new BlobUploadOptions { HttpHeaders = new BlobHttpHeaders { ContentType = contentType } },
                cancellationToken);
            return FileUploadResult.Stored(key);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            // Network errors surface as RequestFailedException, AggregateException (after retries) or
            // HttpRequestException; all are a failed upload for the caller (MSG05), never an exception.
            logger.LogError(exception, "Blob upload of {Key} failed", key);
            return FileUploadResult.Failed("STORAGE_ERROR");
        }
    }

    public async Task<Stream?> OpenReadAsync(string key, CancellationToken cancellationToken = default)
    {
        try
        {
            var download = await _container.Value.GetBlobClient(key).DownloadStreamingAsync(cancellationToken: cancellationToken);
            return download.Value.Content;
        }
        catch (RequestFailedException exception) when (exception.Status == 404)
        {
            return null;
        }
    }

    public Task<string> GetReadUrlAsync(string key, TimeSpan? lifetime = null, CancellationToken cancellationToken = default)
    {
        var blob = _container.Value.GetBlobClient(key);
        var uri = blob.GenerateSasUri(BlobSasPermissions.Read, timeProvider.GetUtcNow() + (lifetime ?? DefaultLifetime));
        return Task.FromResult(uri.ToString());
    }

    public async Task<bool> DeleteAsync(string key, CancellationToken cancellationToken = default)
    {
        try
        {
            var response = await _container.Value.GetBlobClient(key).DeleteIfExistsAsync(cancellationToken: cancellationToken);
            return response.Value;
        }
        catch (RequestFailedException exception)
        {
            logger.LogError(exception, "Blob delete of {Key} failed", key);
            return false;
        }
    }

    private async Task EnsureContainerAsync(CancellationToken cancellationToken)
    {
        if (_containerChecked)
        {
            return;
        }

        await _container.Value.CreateIfNotExistsAsync(PublicAccessType.None, cancellationToken: cancellationToken);
        _containerChecked = true;
    }
}
