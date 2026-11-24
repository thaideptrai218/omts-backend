using Microsoft.EntityFrameworkCore;
using OMTS.Application.Common;
using OMTS.Application.Common.Interfaces;
using OMTS.Application.Features.Purchasing;

namespace OMTS.Infrastructure.Persistence.Queries;

/// <summary>
/// FARM-04 raft material cost (BR-FARM-05): each material usage of the raft costed at the cost price of its item, the price of the
/// Xuất dùng document the usage posted. A usage with an issue takes the cost snapshot of the issue line of its item, per base unit
/// (the line cost is per unit of the line, so it is divided by the unit factor of the line), in the same SQL query as the usages.
/// A usage without one (made before stock was checked) takes the cost of the latest ledger movement of the item up to the end of
/// the usage day (UTC+7), latest time first and then latest written; none gives no price.
/// </summary>
internal sealed class RaftMaterialCostQuery(OmtsDbContext db) : IRaftMaterialCostQuery
{
    /// <summary>
    /// The total of each raft: the amounts of the usages with an issue are summed by SQL (each usage's amount rounded half up to the dong, as <see cref="Amount"/> does, so the quantity and the
    /// cost, both never negative, give floor(x + 0.5)); the few usages without one come as narrow rows and take the ledger cost of their day.
    /// </summary>
    public async Task<IReadOnlyDictionary<Guid, decimal>> GetTotalsAsync(IReadOnlyCollection<Guid> raftIds, CancellationToken cancellationToken)
    {
        var ids = raftIds.Distinct().ToList();
        if (ids.Count == 0)
        {
            return new Dictionary<Guid, decimal>();
        }

        var issueLines = db.InputIssueLines.AsNoTracking();
        var issued = await (
                from u in db.MaterialUsages.AsNoTracking()
                where ids.Contains(u.RaftId) && u.IssueId != null
                from l in issueLines.Where(l => l.IssueId == u.IssueId && l.ItemId == u.ItemId).OrderBy(l => l.LineNumber).Take(1).DefaultIfEmpty()
                select new { u.RaftId, Cost = (decimal?)(l.UnitCost / l.UnitFactor), u.Quantity })
            .GroupBy(x => x.RaftId)
            .Select(g => new { RaftId = g.Key, Total = g.Sum(x => x.Cost == null ? 0m : Math.Floor(x.Quantity * x.Cost.Value + 0.5m)) })
            .ToDictionaryAsync(x => x.RaftId, x => x.Total, cancellationToken);

        var legacy = await db.MaterialUsages.AsNoTracking()
            .Where(u => ids.Contains(u.RaftId) && u.IssueId == null)
            .Select(u => new { u.RaftId, u.ItemId, u.UsageDate, u.Quantity })
            .ToListAsync(cancellationToken);
        var ledgerCosts = await LedgerCostsAsync(legacy.Select(u => (u.ItemId, u.UsageDate)), cancellationToken);
        var legacyTotals = legacy.GroupBy(u => u.RaftId).ToDictionary(g => g.Key, g => g.Sum(u => PurchaseMoney.LineAmount(u.Quantity, ledgerCosts[(u.ItemId, u.UsageDate)])));
        return ids.ToDictionary(id => id, id => issued.GetValueOrDefault(id) + legacyTotals.GetValueOrDefault(id));
    }

    public async Task<IReadOnlyList<RaftUsageCostRow>> UsagesAsync(RaftUsageCostFilter filter, CancellationToken cancellationToken)
    {
        var usages = db.MaterialUsages.AsNoTracking();
        if (filter.From is { } from)
        {
            usages = usages.Where(u => u.UsageDate >= from);
        }

        if (filter.To is { } to)
        {
            usages = usages.Where(u => u.UsageDate <= to);
        }

        if (filter.RaftIds is { } raftIds)
        {
            usages = usages.Where(u => raftIds.Contains(u.RaftId));
        }

        if (filter.ItemId is { } itemId)
        {
            usages = usages.Where(u => u.ItemId == itemId);
        }

        if (filter.ZoneIds is { } zoneIds)
        {
            usages = usages.Where(u => zoneIds.Contains(u.Raft.ZoneId));
        }

        var issueLines = db.InputIssueLines.AsNoTracking();
        var found = await usages
            .Select(u => new Found(
                new RaftUsageCostRow(
                    u.RaftId, u.Raft.Code, u.Raft.ZoneId, u.Raft.Zone.Code, u.Raft.Zone.Name,
                    u.Id, u.UsageDate, u.RepairReason, u.ItemId, u.Item.Code, u.Item.Name, u.Item.Unit, u.Quantity, null),
                u.IssueId,
                issueLines
                    .Where(l => l.IssueId == u.IssueId && l.ItemId == u.ItemId)
                    .OrderBy(l => l.LineNumber)
                    .Select(l => (decimal?)(l.UnitCost / l.UnitFactor))
                    .FirstOrDefault()))
            .ToListAsync(cancellationToken);

        // A usage without an issue (from before stock was checked) takes the ledger cost of its day, read for all its item and day pairs together.
        var ledgerCosts = await LedgerCostsAsync(found.Where(f => f.IssueId is null).Select(f => (f.Row.ItemId, f.Row.UsageDate)), cancellationToken);
        return found.Select(f => f.Row with { UnitPrice = f.IssueId is null ? ledgerCosts[(f.Row.ItemId, f.Row.UsageDate)] : f.IssueUnitCost }).ToList();
    }

    /// <summary>
    /// The cost of one base unit on the latest movement of each item before the end of each business day: latest time first, then latest written; null with no movement or no cost. The end of a day cannot be
    /// worked out in SQL from a date column (a time zone conversion per row does not translate), so each pair is one branch of a few commands that each hold up to <see cref="LedgerBatch"/> of them.
    /// </summary>
    private async Task<Dictionary<(Guid ItemId, DateOnly Day), decimal?>> LedgerCostsAsync(IEnumerable<(Guid ItemId, DateOnly Day)> pairs, CancellationToken cancellationToken)
    {
        var costs = new Dictionary<(Guid ItemId, DateOnly Day), decimal?>();
        foreach (var batch in pairs.Distinct().Chunk(LedgerBatch))
        {
            IQueryable<LedgerCost>? union = null;
            for (var i = 0; i < batch.Length; i++)
            {
                var (itemId, day) = batch[i];
                var endOfDay = BusinessClock.FromLocal(day.AddDays(1).ToDateTime(TimeOnly.MinValue));
                var index = i;
                var one = db.InputStockMovements.AsNoTracking()
                    .Where(m => m.ItemId == itemId && m.OccurredAt < endOfDay)
                    .OrderByDescending(m => m.OccurredAt).ThenByDescending(m => m.Sequence)
                    .Select(m => new LedgerCost { Index = index, UnitCost = m.UnitCost })
                    .Take(1);
                union = union is null ? one : union.Concat(one);
            }

            var found = union is null ? [] : await union.ToListAsync(cancellationToken);
            for (var i = 0; i < batch.Length; i++)
            {
                costs[batch[i]] = found.FirstOrDefault(f => f.Index == i)?.UnitCost;
            }
        }

        return costs;
    }

    private const int LedgerBatch = 50;

    private sealed class LedgerCost
    {
        public int Index { get; init; }

        public decimal? UnitCost { get; init; }
    }

    private sealed record Found(RaftUsageCostRow Row, Guid? IssueId, decimal? IssueUnitCost);

    /// <summary>Quantity × cost half up to the dong (BR-PURCHASE-02); 0 for an item with no cost.</summary>
    public static decimal Amount(RaftUsageCostRow row) => PurchaseMoney.LineAmount(row.Quantity, row.UnitPrice);
}
