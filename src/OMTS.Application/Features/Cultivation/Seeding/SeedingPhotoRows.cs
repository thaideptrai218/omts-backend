using OMTS.Application.Common.Interfaces.Persistence;
using OMTS.Domain.Entities.Cultivation;

namespace OMTS.Application.Features.Cultivation.Seeding;

/// <summary>The photo rows of a seeding session: 1 to <see cref="SeedingRecord.MaxPhotos"/>, one file per slot.</summary>
internal static class SeedingPhotoRows
{
    /// <summary>The photos of a new session, in the lowest free slots.</summary>
    public static void Add(ISeedingRecordRepository records, SeedingRecord record, IReadOnlyList<string> keys)
    {
        var slot = 0;
        foreach (var key in keys)
        {
            var photo = new SeedingRecordPhoto { SeedingRecordId = record.Id, SortOrder = ++slot, FileKey = key };
            record.Photos.Add(photo);
            records.AddPhoto(photo);
        }
    }

    /// <summary>The photos after a correction: the kept ones stay in their slots, the others go, the new files take the free slots. Returns the keys of the removed files.</summary>
    public static List<string> Replace(ISeedingRecordRepository records, SeedingRecord record, IReadOnlyCollection<SeedingRecordPhoto> kept, IReadOnlyList<string> newKeys)
    {
        var removed = record.Photos.Where(p => !kept.Contains(p)).ToList();
        foreach (var photo in removed)
        {
            record.Photos.Remove(photo);
            records.RemovePhoto(photo);
        }

        using var free = Enumerable.Range(1, SeedingRecord.MaxPhotos).Where(slot => kept.All(p => p.SortOrder != slot)).GetEnumerator();
        foreach (var key in newKeys)
        {
            free.MoveNext();
            var photo = new SeedingRecordPhoto { SeedingRecordId = record.Id, SortOrder = free.Current, FileKey = key };
            record.Photos.Add(photo);
            records.AddPhoto(photo);
        }

        return removed.Select(p => p.FileKey).ToList();
    }
}
