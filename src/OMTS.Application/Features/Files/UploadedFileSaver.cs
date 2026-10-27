using Microsoft.Extensions.Logging;
using OMTS.Application.Common.Interfaces.Persistence;
using OMTS.Application.Common.Interfaces.Storage;

namespace OMTS.Application.Features.Files;

/// <summary>
/// Saves a change whose file was uploaded first (FARM raft photos and license files) without leaving files behind:
/// a failed save (database error, MSG06 conflict, duplicate) removes the new upload, and only a committed save removes
/// the file it replaced, so a failed save never loses the current file. Removals are best effort: a failure is logged,
/// never hides the save's own answer, and the file is left to the orphan file clean-up.
/// </summary>
public sealed class UploadedFileSaver(
    IFileStorage storage, IStoredFileReferenceQuery references, IUnitOfWork unitOfWork, ILogger<UploadedFileSaver> logger)
{
    // A slow file store must not hold the answer of the request for long.
    private static readonly TimeSpan RemovalTimeout = TimeSpan.FromSeconds(10);

    /// <param name="uploadedKey">The file stored for this change, if any.</param>
    /// <param name="replacedKey">The file the change stops pointing to, if any.</param>
    /// <param name="cancellationToken">Cancels the save only; removals run on their own timeout.</param>
    public async Task SaveAsync(string? uploadedKey, string? replacedKey, CancellationToken cancellationToken)
    {
        try
        {
            await unitOfWork.SaveChangesAsync(cancellationToken);
        }
        catch
        {
            if (uploadedKey is not null)
            {
                await RemoveAsync(uploadedKey, "after a failed save", checkReference: true);
            }

            throw;
        }

        if (replacedKey is not null && replacedKey != uploadedKey)
        {
            await RemoveAsync(replacedKey, "after it was replaced", checkReference: false);
        }
    }

    private async Task RemoveAsync(string key, string when, bool checkReference)
    {
        using var timeout = new CancellationTokenSource(RemovalTimeout);
        try
        {
            // An error raised after the commit (a lost reply) must not delete the file the saved record points to. When
            // the database cannot answer, the catch below leaves the file to the clean-up, which checks again.
            if (checkReference && await references.IsReferencedAsync(key, timeout.Token))
            {
                logger.LogWarning("Stored file {FileKey} is kept: the failed save was committed and points to it.", key);
                return;
            }

            if (!await storage.DeleteAsync(key, timeout.Token))
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
