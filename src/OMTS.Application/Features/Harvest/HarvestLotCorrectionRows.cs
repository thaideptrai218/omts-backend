using OMTS.Application.Common.Interfaces.Persistence;
using OMTS.Domain.Entities.Harvest;

namespace OMTS.Application.Features.Harvest;

/// <summary>The lane and photo rows of a corrected lot (HARVEST-01.1): what the correction takes out goes, what it adds comes, and what it keeps stays as it is.</summary>
internal static class HarvestLotCorrectionRows
{
    /// <param name="shares">The share each lane of the lot takes (lane id to percent, null: not entered); none (TRACE-03.2): a lane stays as it is and a lane added has none.</param>
    public static void ApplyLanes(IHarvestLotRepository lots, HarvestLot lot, HarvestLotFields fields, IReadOnlyDictionary<Guid, decimal?>? shares = null)
    {
        var wanted = fields.Lanes.ToDictionary(l => l.Id);
        foreach (var lane in lot.Lanes.Where(l => !wanted.ContainsKey(l.LaneId)).ToList())
        {
            lot.Lanes.Remove(lane);
            lots.RemoveLane(lane);
        }

        foreach (var lane in lot.Lanes)
        {
            lane.IsFinished = fields.FinishedLaneIds.Contains(lane.LaneId);
            if (shares is not null && shares.TryGetValue(lane.LaneId, out var share))
            {
                lane.HarvestedPercent = share;
            }
        }

        foreach (var lane in fields.Lanes.Where(l => lot.Lanes.All(existing => existing.LaneId != l.Id)))
        {
            var added = new HarvestLotLane
            {
                LotId = lot.Id, LaneId = lane.Id, Lane = lane, IsFinished = fields.FinishedLaneIds.Contains(lane.Id),
                HarvestedPercent = shares is not null && shares.TryGetValue(lane.Id, out var addedShare) ? addedShare : null,
            };
            lot.Lanes.Add(added);
            lots.AddLane(added);
        }
    }

    /// <summary>Removes the photos not kept and adds the new ones in the free slots; kept photos stay in their slots. Returns the keys of the removed ones.</summary>
    public static List<string> ApplyPhotos(IHarvestLotRepository lots, HarvestLot lot, List<HarvestLotPhoto> kept, IReadOnlyList<string> newKeys)
    {
        var removed = lot.Photos.Except(kept).ToList();
        foreach (var photo in removed)
        {
            lot.Photos.Remove(photo);
            lots.RemovePhoto(photo);
        }

        using var freeSlots = Enumerable.Range(1, HarvestLot.MaxPhotos).Where(slot => kept.All(p => p.SortOrder != slot)).GetEnumerator();
        foreach (var key in newKeys)
        {
            freeSlots.MoveNext();
            var photo = new HarvestLotPhoto { LotId = lot.Id, SortOrder = freeSlots.Current, FileKey = key };
            lot.Photos.Add(photo);
            lots.AddPhoto(photo);
        }

        return removed.Select(p => p.FileKey).ToList();
    }
}
