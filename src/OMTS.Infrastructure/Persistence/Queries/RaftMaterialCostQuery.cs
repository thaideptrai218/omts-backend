using Microsoft.EntityFrameworkCore;
using OMTS.Application.Common.Interfaces;
using OMTS.Application.Features.Purchasing;
using OMTS.Domain.Constants;

namespace OMTS.Infrastructure.Persistence.Queries;

/// <summary>
/// FARM-04 raft material cost (BR-FARM-05): each material usage of the raft costed at the latest purchase price of its item. The
/// price of each usage is one SQL subquery: the Completed receipt lines of the item received on or before the usage date (the
/// date received is the UTC+7 date of the time received, so "on or before" is "not after the end of that day"), latest time
/// received first, then the highest receipt code and line number, as unit price less discount per unit.
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

        var lines = db.PurchaseReceiptLines.AsNoTracking();
        return await usages
            .Select(u => new RaftUsageCostRow(
                u.RaftId, u.Raft.Code, u.Raft.ZoneId, u.Raft.Zone.Code, u.Raft.Zone.Name,
                u.Id, u.UsageDate, u.RepairReason, u.ItemId, u.Item.Code, u.Item.Name, u.Item.Unit, u.Quantity,
                lines
                    .Where(l => l.ItemId == u.ItemId && l.UnitPrice != null
                        && l.Receipt.StatusId == StatusIds.PurchaseReceipt.Completed && l.Receipt.ReceiptDate <= u.UsageDate)
                    .OrderByDescending(l => l.Receipt.ReceivedAt).ThenByDescending(l => l.Receipt.Code).ThenByDescending(l => l.LineNumber)
                    .Select(l => (decimal?)(l.UnitPrice!.Value - l.Discount))
                    .FirstOrDefault()))
            .ToListAsync(cancellationToken);
    }

    /// <summary>Quantity × price half up to the dong (BR-PURCHASE-02); 0 for an item never bought.</summary>
    public static decimal Amount(RaftUsageCostRow row) => PurchaseMoney.LineAmount(row.Quantity, row.UnitPrice);
}
