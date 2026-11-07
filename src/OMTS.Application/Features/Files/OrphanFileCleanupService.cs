using Microsoft.Extensions.Logging;
using OMTS.Application.Common.Interfaces.Persistence;
using OMTS.Application.Common.Interfaces.Storage;
using OMTS.Application.Features.Cultivation.CultivationLogs;

namespace OMTS.Application.Features.Files;

/// <summary>
/// Removes files no record points to: a removal that failed after a replace or a failed save, a host that stopped
/// between upload and save, or a meter photo whose recognition was never saved as a measurement. Only
/// <see cref="Categories"/> are scanned. Cooperative logos never are: every logo file is kept, since published
/// traceability versions show the logo of their publication time (FARM-01). Each environment needs a storage container
/// (or folder) of its own: files its database does not know are deleted.
/// </summary>
public sealed class OrphanFileCleanupService(
    IFileStorage storage,
    IStoredFileReferenceQuery references,
    ICooperativeRepository cooperatives,
    IAuditEventRepository auditEvents,
    ILogger<OrphanFileCleanupService> logger)
{
    /// <summary>
    /// Raft photos, license files, cooperative cover photos, certificate files, cultivation log photos and meter photos: each belongs to one column, so "no
    /// record points to it" is certain.
    /// </summary>
    public static readonly IReadOnlyList<string> Categories =
    [
        FileCategories.RaftPhotos, FileCategories.ZoneLicenses, FileCategories.CooperativeCoverPhotos, FileCategories.CooperativeCertifications, FileCategories.WaterMeterPhotos, FileCategories.CultivationLogPhotos,
    ];

    /// <summary>
    /// Categories whose files may all be unreferenced in a real database: the cover photo was removed, the last certification,
    /// or the last cultivation log. While the database points to no file of the category, it is taken as this environment's
    /// when it holds the cooperative, see <see cref="CleanAsync"/>.
    /// </summary>
    private static readonly IReadOnlySet<string> MayAllBeUnreferenced = new HashSet<string>(StringComparer.Ordinal)
    {
        FileCategories.CooperativeCoverPhotos, FileCategories.CooperativeCertifications, FileCategories.CultivationLogPhotos,
    };

    /// <summary>
    /// Deletes the category's files written before <paramref name="writtenBefore"/> that no record points to, and
    /// returns how many were deleted. Younger files are kept: their upload may still wait for the save pointing to it.
    /// Nothing is deleted (a warning is logged) when the database does not look like this storage's own: when it points
    /// to files of the category but none of them is stored, for raft photos and license files also when it points to none
    /// at all, for cover photos, certificate files and cultivation log photos when it holds no cooperative, and for
    /// cultivation log photos also when it never held a log.
    /// </summary>
    public async Task<int> CleanAsync(string category, DateTimeOffset writtenBefore, CancellationToken cancellationToken)
    {
        if (!Categories.Contains(category))
        {
            throw new ArgumentOutOfRangeException(nameof(category), category, "Only the categories of OrphanFileCleanupService.Categories are cleaned up.");
        }

        var stored = await storage.ListAsync(category, cancellationToken);
        var old = stored.Where(f => f.LastModified < writtenBefore).ToList();
        if (old.Count == 0)
        {
            return 0;
        }

        // Read after the listing: a record saved in between points to a file younger than the grace period. The exception,
        // accepted: a meter photo has no expiry, so one older than that can be recorded in the moment the daily job runs.
        var referenced = await references.ReferencedKeysAsync(category, cancellationToken);
        if (MayAllBeUnreferenced.Contains(category) && referenced.Count == 0)
        {
            if (!await cooperatives.ExistsAsync(cancellationToken))
            {
                // Files of a cooperative but no cooperative: the database belongs to another environment, or is empty.
                logger.LogWarning(
                    "Orphan file clean-up of {Category} skipped: the database holds no cooperative; check that this environment has a storage of its own.",
                    category);
                return 0;
            }

            // Every deployed database holds its cooperative from the bootstrap, so that says little about old log photos. The audit rows of a log
            // outlive it: with none, this database never held a log and these photos are another environment's. After the last real removal they are still there.
            if (category == FileCategories.CultivationLogPhotos && !await auditEvents.AnyOfRecordTypeAsync(CultivationLogAudit.RecordType, cancellationToken))
            {
                logger.LogWarning(
                    "Orphan file clean-up of {Category} skipped: the database never held a cultivation log; check that this environment has a storage of its own.",
                    category);
                return 0;
            }
        }
        else if (!stored.Any(f => referenced.Contains(f.Key)))
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
