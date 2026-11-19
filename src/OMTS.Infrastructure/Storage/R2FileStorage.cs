using Amazon.Runtime;
using Amazon.S3;
using Amazon.S3.Model;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using OMTS.Application.Common.Exceptions;
using OMTS.Application.Common.Interfaces.Storage;
using OMTS.Application.Features.Files;

namespace OMTS.Infrastructure.Storage;

/// <summary>
/// Cloudflare R2 (<c>Storage:Provider = R2</c>) through its S3-compatible API. The bucket stays private: read URLs are pre-signed GET links that expire.
/// The bucket is created once by the operator (see deploy/ec2/README.md), never by the API, so the API token needs object read and write only.
/// </summary>
internal sealed class R2FileStorage(
    IOptions<StorageOptions> options,
    TimeProvider timeProvider,
    ILogger<R2FileStorage> logger) : IFileStorage, IDisposable
{
    private static readonly TimeSpan DefaultLifetime = TimeSpan.FromMinutes(10);

    private AmazonS3Client? _client;

    private R2StorageOptions Settings => options.Value.R2;

    // Created on first use, so a fixed setting works without a restart of the configuration system.
    private AmazonS3Client Client => _client ??= new AmazonS3Client(
        new BasicAWSCredentials(Settings.AccessKeyId, Settings.SecretAccessKey),
        new AmazonS3Config
        {
            ServiceURL = Settings.Endpoint,
            ForcePathStyle = true,
            AuthenticationRegion = "auto",
        });

    public async Task<FileUploadResult> UploadAsync(ValidatedFile file, string category, CancellationToken cancellationToken = default)
    {
        var key = StorageKeys.Create(category, file.Format, timeProvider.GetUtcNow());
        try
        {
            await Client.PutObjectAsync(
                new PutObjectRequest
                {
                    BucketName = Settings.Bucket,
                    Key = key,
                    InputStream = file.File.Content,
                    ContentType = file.ContentType,
                    AutoCloseStream = false,
                    // R2 does not support streaming (chunked) payload signing.
                    DisablePayloadSigning = true,
                },
                cancellationToken);
            return FileUploadResult.Stored(key);
        }
        catch (Exception exception) when (!cancellationToken.IsCancellationRequested)
        {
            logger.LogError(exception, "R2 upload of {Key} failed", key);
            return FileUploadResult.Failed("STORAGE_ERROR");
        }
    }

    public async Task<Stream?> OpenReadAsync(string key, CancellationToken cancellationToken = default)
    {
        if (!StorageKeys.IsValid(key))
        {
            return null;
        }

        try
        {
            var response = await Client.GetObjectAsync(Settings.Bucket, key, cancellationToken);
            return response.ResponseStream;
        }
        catch (AmazonS3Exception exception) when (exception.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            return null;
        }
        catch (Exception exception) when (!cancellationToken.IsCancellationRequested)
        {
            logger.LogError(exception, "R2 read of {Key} failed", key);
            throw new FileStorageUnavailableException(key, exception);
        }
    }

    public Task<string> GetReadUrlAsync(string key, TimeSpan? lifetime = null, CancellationToken cancellationToken = default)
    {
        try
        {
            var url = Client.GetPreSignedURL(new GetPreSignedUrlRequest
            {
                BucketName = Settings.Bucket,
                Key = key,
                Verb = HttpVerb.GET,
                Protocol = Protocol.HTTPS,
                Expires = (timeProvider.GetUtcNow() + (lifetime ?? DefaultLifetime)).UtcDateTime,
            });
            return Task.FromResult(url);
        }
        catch (Exception exception) when (exception is AmazonClientException or ArgumentException or InvalidOperationException)
        {
            logger.LogError(exception, "R2 read URL for {Key} could not be signed", key);
            throw new FileStorageUnavailableException(key, exception);
        }
    }

    public async Task<bool> DeleteAsync(string key, CancellationToken cancellationToken = default)
    {
        try
        {
            // S3 DELETE answers 204 also for a missing key, so check first to report whether something was removed.
            await Client.GetObjectMetadataAsync(Settings.Bucket, key, cancellationToken);
            await Client.DeleteObjectAsync(Settings.Bucket, key, cancellationToken);
            return true;
        }
        catch (AmazonS3Exception exception) when (exception.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            return false;
        }
        catch (Exception exception) when (!cancellationToken.IsCancellationRequested)
        {
            logger.LogError(exception, "R2 delete of {Key} failed", key);
            return false;
        }
    }

    public async Task<IReadOnlyList<StoredFile>> ListAsync(string category, CancellationToken cancellationToken = default)
    {
        var prefix = StorageKeys.ListPrefix(category);
        var files = new List<StoredFile>();
        try
        {
            var request = new ListObjectsV2Request { BucketName = Settings.Bucket, Prefix = prefix };
            ListObjectsV2Response page;
            do
            {
                page = await Client.ListObjectsV2Async(request, cancellationToken);
                foreach (var item in page.S3Objects ?? [])
                {
                    // Objects this provider did not write (no storage key shape) are never listed, so never cleaned up.
                    if (StorageKeys.IsValid(item.Key) && item.LastModified is { } modified)
                    {
                        files.Add(new StoredFile(item.Key, new DateTimeOffset(modified.ToUniversalTime(), TimeSpan.Zero)));
                    }
                }

                request.ContinuationToken = page.NextContinuationToken;
            }
            while (page.IsTruncated == true);

            return files;
        }
        catch (Exception exception) when (!cancellationToken.IsCancellationRequested)
        {
            logger.LogError(exception, "R2 listing of {Category} failed", category);
            throw new FileStorageUnavailableException(prefix, exception);
        }
    }

    public void Dispose() => _client?.Dispose();
}
