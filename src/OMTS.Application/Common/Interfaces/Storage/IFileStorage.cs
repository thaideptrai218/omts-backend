using OMTS.Application.Common.Models.Storage;
using OMTS.Application.Features.Files.Models;
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

    /// <summary>
    /// The stored content, or null when the key does not exist. A provider failure throws
    /// <see cref="Exceptions.FileStorageUnavailableException"/> (502 MSG05 at the API).
    /// </summary>
    Task<Stream?> OpenReadAsync(string key, CancellationToken cancellationToken = default);

    /// <summary>
    /// A URL that allows reading the file until it expires (default 10 minutes). A provider failure throws
    /// <see cref="Exceptions.FileStorageUnavailableException"/>.
    /// </summary>
    Task<string> GetReadUrlAsync(string key, TimeSpan? lifetime = null, CancellationToken cancellationToken = default);

    /// <summary>Removes the file; false when it did not exist or the provider failed.</summary>
    Task<bool> DeleteAsync(string key, CancellationToken cancellationToken = default);

    /// <summary>
    /// Every stored file of the category (keys <c>{category}/...</c> this adapter produced), for the orphan file
    /// clean-up. A provider failure throws <see cref="Exceptions.FileStorageUnavailableException"/>.
    /// </summary>
    Task<IReadOnlyList<StoredFile>> ListAsync(string category, CancellationToken cancellationToken = default);
}
