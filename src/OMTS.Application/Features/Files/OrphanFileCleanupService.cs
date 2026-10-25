using Microsoft.Extensions.Logging;
using OMTS.Application.Common.Interfaces.Persistence;
using OMTS.Application.Common.Interfaces.Storage;

namespace OMTS.Application.Features.Files;

/// <summary>
/// Removes FARM files no record points to: a removal that failed after a replace or a failed save, or a host that
/// stopped between upload and save. Only <see cref="Categories"/> are scanned. Cooperative logos never are: every
/// logo file is kept, since published traceability versions show the logo of their publication time (FARM-01).
/// </summary>
public sealed class OrphanFileCleanupService(IFileStorage storage, IStoredFileReferenceQuery references, ILogger<OrphanFileCleanupService> logger)
{
    /// <summary>Raft photos and license files: each belongs to one column, so "no record points to it" is certain.</summary>
    public static readonly IReadOnlyList<string> Categories = [FileCategories.RaftPhotos, FileCategories.ZoneLicenses];

    /// <summary>
    /// Deletes the category's files written before <paramref name="writtenBefore"/> that no record points to, and
    /// returns how many were deleted. Younger files are kept: their upload may still wait for the save pointing to it.
    /// </summary>
    public async Task<int> CleanAsync(string category, DateTimeOffset writtenBefore, CancellationToken cancellationToken)
    {
        if (!Categories.Contains(category))
        {
            throw new ArgumentOutOfRangeException(nameof(category), category, "Only raft photos and license files are cleaned up.");
        }

        var old = (await storage.ListAsync(category, cancellationToken)).Where(f => f.LastModified < writtenBefore).ToList();
        if (old.Count == 0)
        {
            return 0;
        }

        // Read after the listing: a record saved in between points to a file younger than the grace period.
        var referenced = await references.ReferencedKeysAsync(category, cancellationToken);
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
