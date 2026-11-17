using OMTS.Application.Common.Interfaces.Persistence;
using OMTS.Application.Features.Files;
using OMTS.Domain.Entities.Cultivation;

namespace OMTS.Application.Features.Cultivation.Nursery;

/// <summary>The photo rows of a batch: one phase (start or completion) holds 1 to 3 files, one per slot.</summary>
internal static class NurseryPhotoRows
{
    public static string CategoryOf(NurseryPhotoPhase phase) => phase == NurseryPhotoPhase.Start ? FileCategories.NurseryStartPhotos : FileCategories.NurseryCompletionPhotos;

    /// <summary>The photos of a new batch or of the completion of a Nursing one, in the lowest free slots.</summary>
    public static void Add(INurseryBatchRepository nurseries, NurseryBatch batch, NurseryPhotoPhase phase, IReadOnlyList<string> keys)
    {
        var slot = 0;
        foreach (var key in keys)
        {
            var photo = new NurseryBatchPhoto { NurseryBatchId = batch.Id, Phase = phase, SortOrder = ++slot, FileKey = key };
            batch.Photos.Add(photo);
            nurseries.AddPhoto(photo);
        }
    }

    /// <summary>The photos of a phase after an edit: the kept ones stay, the others go, the new files take the free slots. Returns the keys of the removed files.</summary>
    public static List<string> Replace(INurseryBatchRepository nurseries, NurseryBatch batch, NurseryPhotoPhase phase, IReadOnlyCollection<NurseryBatchPhoto> kept, IReadOnlyList<string> newKeys)
    {
        var removed = batch.Photos.Where(p => p.Phase == phase && !kept.Contains(p)).ToList();
        foreach (var photo in removed)
        {
            batch.Photos.Remove(photo);
            nurseries.RemovePhoto(photo);
        }

        using var free = Enumerable.Range(1, NurseryBatch.MaxPhotos).Where(slot => kept.All(p => p.SortOrder != slot)).GetEnumerator();
        foreach (var key in newKeys)
        {
            free.MoveNext();
            var photo = new NurseryBatchPhoto { NurseryBatchId = batch.Id, Phase = phase, SortOrder = free.Current, FileKey = key };
            batch.Photos.Add(photo);
            nurseries.AddPhoto(photo);
        }

        return removed.Select(p => p.FileKey).ToList();
    }
}
