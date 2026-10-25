using Microsoft.Extensions.Logging;
using OMTS.Application.Common.Interfaces.Persistence;
using OMTS.Application.Common.Interfaces.Storage;

namespace OMTS.Application.Features.Files;

/// <summary>
/// Saves a change whose file was uploaded first (FARM raft photos and license files) without leaving files behind:
/// a failed save (database error, MSG06 conflict, duplicate) removes the new upload, and only a committed save removes
/// the file it replaced, so a failed save never loses the current file. Removals are best effort: a failure is logged,
/// never hides the save's own error, and the file is left to the orphan file clean-up.
/// </summary>
public sealed class UploadedFileSaver(IFileStorage storage, IUnitOfWork unitOfWork, ILogger<UploadedFileSaver> logger)
{
    /// <param name="uploadedKey">The file stored for this change, if any.</param>
    /// <param name="replacedKey">The file the change stops pointing to, if any.</param>
    /// <param name="cancellationToken">Cancels the save only; removals always run to the end.</param>
    public async Task SaveAsync(string? uploadedKey, string? replacedKey, CancellationToken cancellationToken)
    {
        try
        {
            await unitOfWork.SaveChangesAsync(cancellationToken);
        }
        catch
        {
            await RemoveAsync(uploadedKey, "after a failed save");
            throw;
        }

        if (replacedKey != uploadedKey)
        {
            await RemoveAsync(replacedKey, "after it was replaced");
        }
    }

    private async Task RemoveAsync(string? key, string when)
    {
        if (key is null)
        {
            return;
        }

        try
        {
            if (!await storage.DeleteAsync(key, CancellationToken.None))
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
