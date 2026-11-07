using Microsoft.EntityFrameworkCore;
using OMTS.Application.Common.Interfaces;
using OMTS.Domain.Constants;
using OMTS.Domain.Enums;

namespace OMTS.Infrastructure.Persistence.Queries;

/// <summary>
/// FARM-04 raft material cost (BR-FARM-05): Σ of the Material lines of Completed purchase receipts that name the raft,
/// minus the amounts returned on those lines by returns that are not cancelled; Seed lines that name the raft are not
/// material cost. Summed in SQL.
/// </summary>
internal sealed class RaftMaterialCostQuery(OmtsDbContext db) : IRaftMaterialCostQuery
{
    public async Task<IReadOnlyDictionary<Guid, decimal>?> GetTotalsAsync(IReadOnlyCollection<Guid> raftIds, CancellationToken cancellationToken)
    {
        var lines = db.PurchaseReceiptLines.AsNoTracking()
            .Where(l => l.RaftId != null && raftIds.Contains(l.RaftId.Value) && l.Item != null && l.Item.Group == InputItemGroup.Material
                && l.Receipt.StatusId == StatusIds.PurchaseReceipt.Completed);
        var bought = await lines.GroupBy(l => l.RaftId!.Value)
            .Select(g => new { g.Key, Amount = g.Sum(l => l.LineAmount) })
            .ToDictionaryAsync(x => x.Key, x => x.Amount, cancellationToken);
        var lineIds = lines.Select(l => l.Id);
        var returned = await db.RecordedReturnLines()
            .Where(rl => lineIds.Contains(rl.ReceiptLineId))
            .GroupBy(rl => rl.ReceiptLine.RaftId!.Value)
            .Select(g => new { g.Key, Amount = g.Sum(rl => rl.ReturnValue) })
            .ToDictionaryAsync(x => x.Key, x => x.Amount, cancellationToken);
        return raftIds.ToDictionary(id => id, id => bought.GetValueOrDefault(id) - returned.GetValueOrDefault(id));
    }
}
