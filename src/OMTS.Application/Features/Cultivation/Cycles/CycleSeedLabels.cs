using OMTS.Application.Common.Interfaces.Persistence;
using OMTS.Application.Features.Purchasing;
using OMTS.Domain.Entities.Cultivation;

namespace OMTS.Application.Features.Cultivation.Cycles;

/// <summary>The seed lot a cycle farms, as a label needs it: the code written on the bags (PN000013-1) and the seed item.</summary>
public sealed record CycleSeedLot(string Code, string? ItemName);

/// <summary>
/// What a raft is farming, named by its seed (owner feedback of 13/11/2026: "Đang nuôi: Chưa xác định giống" on a raft that is farming).
/// <paramref name="SeedItemNames"/> is set only when a legacy cycle was seeded from lots of more than one seed item (distinct names, the one with most seed first).
/// <paramref name="PrimaryLotLineId"/> is the receipt line of <paramref name="SeedLot"/>, for a caller that needs the whole lot.
/// </summary>
public sealed record CycleSeedLabel(string? NurseryCode, string? SeedItemName, CycleSeedLot? SeedLot, IReadOnlyList<string>? SeedItemNames, Guid? PrimaryLotLineId);

/// <summary>
/// One seed lot a cycle drew from. <paramref name="FromBatch"/>: it is the lot of the nursery batch of the cycle (then <paramref name="NurseryCode"/> is set and <paramref name="Quantity"/> is 0);
/// otherwise it is a lot of the cycle's seeding records, <paramref name="Quantity"/> the total seeded from it.
/// </summary>
public sealed record CycleSeedSource(Guid CycleId, Guid LotLineId, string ReceiptCode, int LineNumber, string? ItemName, string? NurseryCode, decimal Quantity, bool FromBatch);

/// <summary>
/// Names the seed of cycles. A cycle started from a nursery batch farms the batch's seed lot and item; a legacy cycle (no batch) farms what its seeding records hung: the lot with the most seed is the
/// primary one and the distinct item names are listed. The same rule everywhere a cycle is read, so a label never says "unknown" for a cycle that has seed.
/// </summary>
public sealed class CycleSeedLabels(INurseryBatchRepository nurseries)
{
    /// <summary>One batch of queries for any number of cycles (no query per raft); a cycle with no batch and no seeding record has no entry.</summary>
    public async Task<IReadOnlyDictionary<Guid, CycleSeedLabel>> ReadAsync(IReadOnlyCollection<Guid> cycleIds, CancellationToken cancellationToken)
    {
        if (cycleIds.Count == 0)
        {
            return new Dictionary<Guid, CycleSeedLabel>();
        }

        var sources = await nurseries.SeedSourcesAsync(cycleIds, cancellationToken);
        return sources.GroupBy(s => s.CycleId).ToDictionary(g => g.Key, g => Build(g)!);
    }

    /// <summary>The sources of a legacy cycle, from its seeding records (lot, receipt and item loaded).</summary>
    public static IEnumerable<CycleSeedSource> FromRecords(IEnumerable<SeedingRecord> records) => records
        .GroupBy(r => r.SeedLotLineId)
        .Select(g =>
        {
            var line = g.First().SeedLotLine;
            return new CycleSeedSource(g.First().CycleId, line.Id, line.Receipt.Code, line.LineNumber, line.Item?.Name, null, g.Sum(r => r.Quantity), false);
        });

    /// <summary>The batch's lot wins; else the lot with the most seed (ties by lot code). Null when there is nothing to name.</summary>
    public static CycleSeedLabel? Build(IEnumerable<CycleSeedSource> sources)
    {
        var all = sources.ToList();
        var batch = all.FirstOrDefault(s => s.FromBatch);
        var primary = batch ?? all.OrderByDescending(s => s.Quantity).ThenBy(s => DocumentCodes.SeedLotCode(s.ReceiptCode, s.LineNumber), StringComparer.Ordinal).FirstOrDefault();
        if (primary is null)
        {
            return null;
        }

        var names = (batch is not null ? new List<CycleSeedSource> { batch } : all.OrderByDescending(s => s.Quantity).ToList())
            .Select(s => s.ItemName).Where(n => !string.IsNullOrWhiteSpace(n)).Select(n => n!).Distinct(StringComparer.Ordinal).ToList();
        return new CycleSeedLabel(
            batch?.NurseryCode,
            primary.ItemName ?? names.FirstOrDefault(),
            new CycleSeedLot(DocumentCodes.SeedLotCode(primary.ReceiptCode, primary.LineNumber), primary.ItemName),
            names.Count > 1 ? names : null,
            primary.LotLineId);
    }
}
