using OMTS.Application.Common.Interfaces.Persistence;
using OMTS.Application.Common.Messages;

namespace OMTS.Application.Features.Traceability.Publishing;

/// <summary>
/// One lane of a harvest lot with one seeding session of the lot's cycle that hung seed on it. A lane with no such session has one row with no seed lot.
/// </summary>
/// <param name="SeedLotCode">The seed lot of the session (PN000123-1), or null when the lane has no session in the cycle.</param>
/// <param name="OnCompletedReceipt">The seed lot is a line of a Completed purchase receipt, not a Cancelled one (GBR-04).</param>
public sealed record LotLaneSeedLot(Guid LotId, int LaneNumber, string? SeedLotCode, bool OnCompletedReceipt);

/// <summary>
/// BR-TRACE-01: a harvest lot has all the origin information the provenance page needs when every lane of the lot has a seed lot, that is a seeding session in the lot's cycle whose
/// seed lot is a line of a completed purchase receipt that is not cancelled. The raft, the zone and the cycle are always known; photos and the map pin are not needed. The system names
/// what is missing and never fills it in (BR-TRACE-03).
/// </summary>
public sealed class ProvenanceReadiness(IHarvestLotRepository lots)
{
    /// <summary>What each lot misses, in lane order; an empty list when it is ready. Every id asked for has an entry.</summary>
    public async Task<IReadOnlyDictionary<Guid, IReadOnlyList<OriginGap>>> MissingAsync(IReadOnlyCollection<Guid> lotIds, CancellationToken cancellationToken)
    {
        var ids = lotIds.Distinct().ToList();
        IReadOnlyList<LotLaneSeedLot> rows = ids.Count == 0 ? [] : await lots.LaneSeedLotsAsync(ids, cancellationToken);
        var byLot = rows.ToLookup(r => r.LotId);
        return ids.ToDictionary(id => id, id => Missing(byLot[id]));
    }

    /// <summary>The missing items of one lot from the rows of its lanes (the {missing_items} of MSG-TRACE-01).</summary>
    public static IReadOnlyList<OriginGap> Missing(IEnumerable<LotLaneSeedLot> rowsOfOneLot) => rowsOfOneLot
        .GroupBy(r => r.LaneNumber)
        .OrderBy(g => g.Key)
        .Where(lane => !lane.Any(r => r.SeedLotCode is not null && r.OnCompletedReceipt))
        .Select(lane =>
        {
            var codes = lane.Where(r => r.SeedLotCode is not null).Select(r => r.SeedLotCode!).Distinct().Order(StringComparer.Ordinal).ToList();
            return codes.Count == 0 ? OriginGap.NoSeedingSession(lane.Key) : OriginGap.SeedLotNotOnCompletedReceipt(lane.Key, codes);
        })
        .ToList();

    /// <summary>
    /// TRACE-02.1 / BR-TRACE-03 (the {missing_items} of MSG-TRACE-02): every seed lot of a lane is traced, so a seed lot not on a completed receipt is named even when the lane has
    /// another valid one (stricter than <see cref="Missing"/>, which only decides whether the lot can be published).
    /// </summary>
    public static IReadOnlyList<OriginGap> MissingInTrace(IEnumerable<LotLaneSeedLot> rowsOfOneLot) => rowsOfOneLot
        .GroupBy(r => r.LaneNumber)
        .OrderBy(g => g.Key)
        .Select(lane =>
        {
            if (!lane.Any(r => r.SeedLotCode is not null))
            {
                return OriginGap.NoSeedingSession(lane.Key);
            }

            var broken = lane.Where(r => r.SeedLotCode is not null && !r.OnCompletedReceipt).Select(r => r.SeedLotCode!).Distinct().Order(StringComparer.Ordinal).ToList();
            return broken.Count == 0 ? null : OriginGap.SeedLotNotOnCompletedReceipt(lane.Key, broken);
        })
        .OfType<OriginGap>()
        .ToList();
}
