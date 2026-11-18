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
    public async Task<IReadOnlyDictionary<Guid, decimal>> GetTotalsAsync(IReadOnlyCollection<Guid> raftIds, CancellationToken cancellationToken)
    {
        var rows = await UsagesAsync(new RaftUsageCostFilter(RaftIds: raftIds), cancellationToken);
        var totals = rows.GroupBy(r => r.RaftId).ToDictionary(g => g.Key, g => g.Sum(Amount));
        return raftIds.Distinct().ToDictionary(id => id, id => totals.GetValueOrDefault(id));
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

        // The cost on the ledger at the end of a day cannot be worked out in SQL from a date column (a time zone conversion per row does not translate), so a usage without an
        // issue asks for it once per item and day: such usages only exist from before stock was checked.
        var ledgerCosts = new Dictionary<(Guid ItemId, DateOnly Day), decimal?>();
        foreach (var key in found.Where(f => f.IssueId is null).Select(f => (f.Row.ItemId, f.Row.UsageDate)).Distinct())
        {
            ledgerCosts[key] = await LedgerCostAsync(key.ItemId, key.UsageDate, cancellationToken);
        }

        return found.Select(f => f.Row with { UnitPrice = f.IssueId is null ? ledgerCosts[(f.Row.ItemId, f.Row.UsageDate)] : f.IssueUnitCost }).ToList();
    }

    /// <summary>The cost of one base unit on the latest movement of the item before the end of the business day: latest time first, then latest written; null with no movement or no cost.</summary>
    private async Task<decimal?> LedgerCostAsync(Guid itemId, DateOnly day, CancellationToken cancellationToken)
    {
        var endOfDay = BusinessClock.FromLocal(day.AddDays(1).ToDateTime(TimeOnly.MinValue));
        return await db.InputStockMovements.AsNoTracking()
            .Where(m => m.ItemId == itemId && m.OccurredAt < endOfDay)
            .OrderByDescending(m => m.OccurredAt).ThenByDescending(m => m.Sequence)
            .Select(m => m.UnitCost)
            .FirstOrDefaultAsync(cancellationToken);
    }

    private sealed record Found(RaftUsageCostRow Row, Guid? IssueId, decimal? IssueUnitCost);

    /// <summary>Quantity × cost half up to the dong (BR-PURCHASE-02); 0 for an item with no cost.</summary>
    public static decimal Amount(RaftUsageCostRow row) => PurchaseMoney.LineAmount(row.Quantity, row.UnitPrice);
}
