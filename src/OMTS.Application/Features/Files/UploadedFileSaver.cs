using Microsoft.Extensions.Logging;
using OMTS.Application.Common.Interfaces.Persistence;
using OMTS.Application.Common.Interfaces.Storage;

namespace OMTS.Application.Features.Files;

/// <summary>
/// Saves a change whose files were uploaded first (FARM raft photos and license files, CYCLE-04 log photos) without leaving files
/// behind: a failed save (database error, MSG06 conflict, duplicate) removes the new uploads, and only a committed save removes
/// the files it replaced, so a failed save never loses a current file. Removals are best effort: a failure is logged,
/// never hides the save's own answer, and the file is left to the orphan file clean-up.
/// </summary>
public sealed class UploadedFileSaver(
    IFileStorage storage, IStoredFileReferenceQuery references, IUnitOfWork unitOfWork, ILogger<UploadedFileSaver> logger)
{
    // A slow file store must not hold the answer of the request for long, however many files the change has.
    private static readonly TimeSpan RemovalTimeout = TimeSpan.FromSeconds(10);

    /// <param name="uploadedKey">The file stored for this change, if any.</param>
    /// <param name="replacedKey">The file the change stops pointing to, if any.</param>
    /// <param name="cancellationToken">Cancels the save only; removals run on their own timeout.</param>
    public Task SaveAsync(string? uploadedKey, string? replacedKey, CancellationToken cancellationToken) => SaveAsync(
        uploadedKey is null ? [] : [uploadedKey],
        replacedKey is null || replacedKey == uploadedKey ? [] : [replacedKey],
        cancellationToken);

    /// <param name="uploadedKeys">The files stored for this change.</param>
    /// <param name="replacedKeys">The files the change stops pointing to.</param>
    /// <param name="cancellationToken">Cancels the save only; removals run on their own timeout.</param>
    public async Task SaveAsync(IReadOnlyCollection<string> uploadedKeys, IReadOnlyCollection<string> replacedKeys, CancellationToken cancellationToken)
    {
        try
        {
            await unitOfWork.SaveChangesAsync(cancellationToken);
        }
        catch
        {
            await RemoveAsync(uploadedKeys, "after a failed save", checkReference: true);
            throw;
        }

        await RemoveAsync(replacedKeys, "after it was replaced", checkReference: false);
    }

    /// <summary>Removes the files stored for a change that will not be saved, as when a later file of the change could not be uploaded.</summary>
    public Task DiscardAsync(IReadOnlyCollection<string> uploadedKeys) => RemoveAsync(uploadedKeys, "after a failed upload", checkReference: false);

    private async Task RemoveAsync(IReadOnlyCollection<string> keys, string when, bool checkReference)
    {
        if (keys.Count == 0)
        {
            return;
        }

        using var timeout = new CancellationTokenSource(RemovalTimeout);
        foreach (var key in keys)
        {
            await RemoveAsync(key, when, checkReference, timeout.Token);
        }
    }

    private async Task RemoveAsync(string key, string when, bool checkReference, CancellationToken timeout)
    {
        try
        {
            // An error raised after the commit (a lost reply) must not delete the file the saved record points to. When
            // the database cannot answer, the catch below leaves the file to the clean-up, which checks again.
            if (checkReference && await references.IsReferencedAsync(key, timeout))
            {
                logger.LogWarning("Stored file {FileKey} is kept: the failed save was committed and points to it.", key);
                return;
            }

            if (!await storage.DeleteAsync(key, timeout))
            {
                logger.LogWarning("Stored file {FileKey} could not be removed {When}; the orphan file clean-up will retry.", key, when);
            }
        }
        catch (Exception exception)
        {
            logger.LogWarning(exception, "Stored file {FileKey} could not be removed {When}; the orphan file clean-up will retry.", key, when);
        }
    }
}
