using OMTS.Application.Common.Interfaces.Persistence;
using OMTS.Domain.Entities.Cultivation;

namespace OMTS.Application.Features.Cultivation.CultivationLogs;

/// <summary>The photo rows of a corrected log, in CYCLE-04.1 and in TRACE-03.1: kept photos stay in their slots, the others go, and new ones take the free slots.</summary>
internal static class CultivationLogPhotoRows
{
    /// <summary>Returns the keys of the removed photos, whose files go once the save commits.</summary>
    public static List<string> Apply(ICultivationLogRepository logs, CultivationLog log, List<CultivationLogPhoto> kept, IReadOnlyList<string> newKeys)
    {
        var removed = log.Photos.Except(kept).ToList();
        foreach (var photo in removed)
        {
            log.Photos.Remove(photo);
            logs.RemovePhoto(photo);
        }

        using var freeSlots = Enumerable.Range(1, CultivationLog.MaxPhotos).Where(slot => kept.All(p => p.SortOrder != slot)).GetEnumerator();
        foreach (var key in newKeys)
        {
            freeSlots.MoveNext();
            var photo = new CultivationLogPhoto { LogId = log.Id, SortOrder = freeSlots.Current, FileKey = key };
            log.Photos.Add(photo);
            logs.AddPhoto(photo);
        }

        return removed.Select(p => p.FileKey).ToList();
    }
}
