using Microsoft.EntityFrameworkCore;
using OMTS.Application.Common.Interfaces;
using OMTS.Application.Features.Purchasing;
using OMTS.Domain.Constants;

namespace OMTS.Infrastructure.Persistence.Queries;

/// <summary>FARM-04 raft material cost: Σ over the raft's Completed receipts of their raft material cost (INP-03).</summary>
internal sealed class RaftMaterialCostQuery(OmtsDbContext db) : IRaftMaterialCostQuery
{
    public async Task<IReadOnlyDictionary<Guid, decimal>?> GetTotalsAsync(IReadOnlyCollection<Guid> raftIds, CancellationToken cancellationToken)
    {
        var receipts = await db.PurchaseReceipts.AsNoTracking()
            .Include(r => r.Lines).ThenInclude(l => l.Item)
            .Include(r => r.Returns).ThenInclude(rt => rt.Lines)
            .Where(r => r.RaftId != null && raftIds.Contains(r.RaftId.Value) && r.StatusId == StatusIds.PurchaseReceipt.Completed)
            .AsSplitQuery()
            .ToListAsync(cancellationToken);
        var totals = receipts.GroupBy(r => r.RaftId!.Value).ToDictionary(g => g.Key, g => g.Sum(PurchaseMoney.RaftMaterialCost));
        return raftIds.ToDictionary(id => id, id => totals.GetValueOrDefault(id));
    }
}
