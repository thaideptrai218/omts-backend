using OMTS.Application.Features.Files;

namespace OMTS.Application.Common.Interfaces.Storage;

/// <summary>
/// Cloud Storage Service adapter (formerly API-06); provider chosen by <c>Storage:Provider</c>.
/// Entities store the returned key in their <c>...Url</c> columns; read URLs are short-lived and are only
/// handed out by endpoints that already checked the caller's permission on the owning record.
/// Containers are never public.
/// </summary>
public interface IFileStorage
{
    /// <summary>
    /// Stores the content unchanged under a new key <c>{category}/{yyyy}/{MM}/{guid}{ext}</c>. Only files
    /// that passed <see cref="FileValidator"/> can be stored: extension and content type come from the
    /// detected format, never from the client's file name. A provider failure is returned, never thrown.
    /// </summary>
    Task<FileUploadResult> UploadAsync(ValidatedFile file, string category, CancellationToken cancellationToken = default);

    /// <summary>The stored content, or null when the key does not exist.</summary>
    Task<Stream?> OpenReadAsync(string key, CancellationToken cancellationToken = default);

    /// <summary>A URL that allows reading the file until it expires (default 10 minutes).</summary>
    Task<string> GetReadUrlAsync(string key, TimeSpan? lifetime = null, CancellationToken cancellationToken = default);

    /// <summary>Removes the file; false when it did not exist or the provider failed.</summary>
    Task<bool> DeleteAsync(string key, CancellationToken cancellationToken = default);
}

public sealed record FileUploadResult(bool Succeeded, string? Key, string? ErrorCode)
{
    public static FileUploadResult Stored(string key) => new(true, key, null);

    public static FileUploadResult Failed(string errorCode) => new(false, null, errorCode);
}
