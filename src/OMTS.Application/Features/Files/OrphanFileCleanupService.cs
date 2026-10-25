using Microsoft.Extensions.Logging;
using OMTS.Application.Common.Interfaces.Persistence;
using OMTS.Application.Common.Interfaces.Storage;

namespace OMTS.Application.Features.Files;

/// <summary>
/// Removes FARM files no record points to: a removal that failed after a replace or a failed save, or a host that
/// stopped between upload and save. Only <see cref="Categories"/> are scanned. Cooperative logos never are: every
/// logo file is kept, since published traceability versions show the logo of their publication time (FARM-01).
/// Each environment needs a storage container (or folder) of its own: files its database does not know are deleted.
/// </summary>
public sealed class OrphanFileCleanupService(IFileStorage storage, IStoredFileReferenceQuery references, ILogger<OrphanFileCleanupService> logger)
{
    /// <summary>Raft photos and license files: each belongs to one column, so "no record points to it" is certain.</summary>
    public static readonly IReadOnlyList<string> Categories = [FileCategories.RaftPhotos, FileCategories.ZoneLicenses];

    /// <summary>
    /// Deletes the category's files written before <paramref name="writtenBefore"/> that no record points to, and
    /// returns how many were deleted. Younger files are kept: their upload may still wait for the save pointing to it.
    /// When no stored file of the category at all is referenced, nothing is deleted (a warning is logged).
    /// </summary>
    public async Task<int> CleanAsync(string category, DateTimeOffset writtenBefore, CancellationToken cancellationToken)
    {
        if (!Categories.Contains(category))
        {
            throw new ArgumentOutOfRangeException(nameof(category), category, "Only raft photos and license files are cleaned up.");
        }

        var stored = await storage.ListAsync(category, cancellationToken);
        var old = stored.Where(f => f.LastModified < writtenBefore).ToList();
        if (old.Count == 0)
        {
            return 0;
        }

        // Read after the listing: a record saved in between points to a file younger than the grace period.
        var referenced = await references.ReferencedKeysAsync(category, cancellationToken);
        if (!stored.Any(f => referenced.Contains(f.Key)))
        {
            // Not one stored file (old or new) is referenced: the database belongs to another environment, or is empty,
            // rather than this storage being full of orphans. Deleting would empty the folder, so nothing is deleted.
            logger.LogWarning(
                "Orphan file clean-up of {Category} skipped: none of its {Count} stored files is referenced by the database; check that this environment has a storage of its own.",
                category, stored.Count);
            return 0;
        }

        var deleted = 0;
        foreach (var orphan in old.Where(f => !referenced.Contains(f.Key)))
        {
            if (await storage.DeleteAsync(orphan.Key, cancellationToken))
            {
                deleted++;
                logger.LogInformation("Orphan file {FileKey} written {WrittenAt:O} deleted.", orphan.Key, orphan.LastModified);
            }
            else
            {
                logger.LogWarning("Orphan file {FileKey} could not be deleted; the next run retries.", orphan.Key);
            }
        }

        return deleted;
    }
}
