using Microsoft.EntityFrameworkCore;
using OMTS.Application.Common.Interfaces.Persistence;
using OMTS.Domain.Constants;
using OMTS.Domain.Entities.Purchasing;
using OMTS.Domain.Enums;

namespace OMTS.Infrastructure.Persistence.Repositories;

internal sealed class SeedLotRepository(OmtsDbContext db) : ISeedLotRepository, ISeedLotUsageQuery
{
    public Task<PurchaseReceiptLine?> GetForSeedingAsync(Guid lineId, CancellationToken cancellationToken) =>
        db.PurchaseReceiptLines
            .Include(l => l.Item)
            .Include(l => l.Receipt).ThenInclude(r => r.Supplier)
            .FirstOrDefaultAsync(l => l.Id == lineId, cancellationToken);

    public async Task<decimal> ReturnedAsync(Guid lineId, CancellationToken cancellationToken) =>
        await db.PurchaseReturnLines.AsNoTracking()
            .Where(l => l.ReceiptLineId == lineId && l.Return.StatusId == StatusIds.PurchaseReturn.Recorded)
            .SumAsync(l => l.Quantity, cancellationToken);

    public async Task<IReadOnlyList<SeedLotRow>> ListOfferedAsync(Guid raftId, CancellationToken cancellationToken)
    {
        var rows = await db.PurchaseReceiptLines.AsNoTracking()
            .Where(l => l.Receipt.StatusId == StatusIds.PurchaseReceipt.Completed && l.Item != null && l.Item.Group == InputItemGroup.Seed)
            .Select(l => new
            {
                l.Id,
                l.LineNumber,
                l.ReceiptId,
                l.Receipt.Code,
                l.Receipt.ReceiptDate,
                l.ItemId,
                ItemCode = l.Item!.Code,
                ItemName = l.Item.Name,
                // Seeds are counted in base units, so a lot received in a bag still reads in the base unit of the item.
                Unit = l.Item.Unit,
                l.Receipt.SupplierId,
                SupplierName = l.Receipt.Supplier.Name,
                l.RaftId,
                Received = l.Quantity ?? 0,
                Seeded = db.SeedingRecords.Where(s => s.SeedLotLineId == l.Id).Sum(s => (decimal?)s.Quantity) ?? 0,
                Returned = db.PurchaseReturnLines.Where(r => r.ReceiptLineId == l.Id && r.Return.StatusId == StatusIds.PurchaseReturn.Recorded).Sum(r => (decimal?)r.Quantity) ?? 0,
            })
            .Where(l => l.Returned < l.Received)
            .ToListAsync(cancellationToken);

        var consumed = await SeededAsync(rows.Select(r => r.Id).ToList(), cancellationToken);
        return rows
            .OrderByDescending(l => l.RaftId == raftId).ThenByDescending(l => l.ReceiptDate).ThenByDescending(l => l.Code, StringComparer.Ordinal).ThenBy(l => l.LineNumber)
            .Select(l => new SeedLotRow(
                l.Id, l.LineNumber, l.ReceiptId, l.Code, l.ReceiptDate, l.ItemId, l.ItemCode, l.ItemName, l.Unit, l.SupplierId, l.SupplierName, l.RaftId, l.Received, l.Seeded,
                l.Returned, consumed.GetValueOrDefault(l.Id)))
            .ToList();
    }

    /// <summary>
    /// What each seed lot has really consumed (PM decision 11/11/2026): the net quantity of its use and disposal issues on the ledger (a seeding session posts one, so it counts once), plus the
    /// sessions recorded before stock was checked, which have no issue. Lots with nothing consumed are left out.
    /// </summary>
    public async Task<IReadOnlyDictionary<Guid, decimal>> SeededAsync(IReadOnlyCollection<Guid> lineIds, CancellationToken cancellationToken)
    {
        var legacy = await db.SeedingRecords.AsNoTracking()
            .Where(r => lineIds.Contains(r.SeedLotLineId) && r.IssueId == null)
            .Select(r => new { r.SeedLotLineId, r.Quantity })
            .ToListAsync(cancellationToken);
        var issued = await db.InputStockMovements.AsNoTracking()
            .Where(m => m.SeedLotLineId != null && lineIds.Contains(m.SeedLotLineId.Value) && db.InputIssues.Any(i => i.Id == m.DocumentId))
            .Select(m => new { LineId = m.SeedLotLineId!.Value, m.Quantity })
            .ToListAsync(cancellationToken);
        var total = new Dictionary<Guid, decimal>();
        foreach (var row in legacy)
        {
            total[row.SeedLotLineId] = total.GetValueOrDefault(row.SeedLotLineId) + row.Quantity;
        }

        foreach (var row in issued)
        {
            // Issue rows are negative; a reversal of one is positive, so the sum is what is still out of the stock.
            total[row.LineId] = total.GetValueOrDefault(row.LineId) - row.Quantity;
        }

        return total.Where(t => t.Value > 0m).ToDictionary(t => t.Key, t => t.Value);
    }

    public async Task<DateOnly?> FirstSeedingDateAsync(IReadOnlyCollection<Guid> lineIds, CancellationToken cancellationToken) =>
        await db.SeedingRecords.AsNoTracking()
            .Where(r => lineIds.Contains(r.SeedLotLineId))
            .MinAsync(r => (DateOnly?)r.SeedingDate, cancellationToken);
}

