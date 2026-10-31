using Microsoft.EntityFrameworkCore;
using OMTS.Application.Common.Interfaces.Persistence;
using OMTS.Application.Common.Models.Persistence;
using OMTS.Domain.Constants;
using OMTS.Domain.Entities.Purchasing;
using OMTS.Domain.Enums;

namespace OMTS.Infrastructure.Persistence.Repositories.Purchasing;

internal sealed partial class PurchaseReceiptRepository(OmtsDbContext db) : IPurchaseReceiptRepository
{
    public Task<PurchaseReceipt?> GetForEditAsync(Guid receiptId, CancellationToken cancellationToken) =>
        db.PurchaseReceipts
            .Include(r => r.Supplier)
            .Include(r => r.Zone)
            .Include(r => r.Raft)
            .Include(r => r.Lines).ThenInclude(l => l.Item)
            .Include(r => r.Lines).ThenInclude(l => l.SeedLot)
            .Include(r => r.Returns).ThenInclude(rt => rt.Lines)
            .Include(r => r.Payments)
            .AsSplitQuery()
            .FirstOrDefaultAsync(r => r.Id == receiptId, cancellationToken);

    public Task<int> CountCodesAsync(string prefix, CancellationToken cancellationToken) =>
        db.PurchaseReceipts.CountAsync(r => r.Code.StartsWith(prefix), cancellationToken);

    public Task<int> CountReturnCodesAsync(string prefix, CancellationToken cancellationToken) =>
        db.PurchaseReturns.CountAsync(r => r.Code.StartsWith(prefix), cancellationToken);

    public async Task<IReadOnlyDictionary<Guid, (decimal Stocked, bool UnknownQuantity)>> StockedAsync(
        IReadOnlyCollection<Guid> lineIds, CancellationToken cancellationToken)
    {
        var rows = await db.SeedingEvents.AsNoTracking()
            .Where(e => lineIds.Contains(e.SeedLot.PurchaseReceiptLineId))
            .Select(e => new { e.SeedLot.PurchaseReceiptLineId, e.SeedQuantity })
            .ToListAsync(cancellationToken);
        return rows.GroupBy(r => r.PurchaseReceiptLineId)
            .ToDictionary(g => g.Key, g => (g.Sum(r => r.SeedQuantity ?? 0), g.Any(r => r.SeedQuantity is null)));
    }

    public async Task<IReadOnlyList<PurchaseReceipt>> SearchAsync(PurchaseReceiptFilter filter, CancellationToken cancellationToken) =>
        await Filtered(filter, WithFigures(db.PurchaseReceipts.AsNoTracking()).Include(r => r.Supplier).Include(r => r.Zone).Include(r => r.Raft))
            .OrderByDescending(r => r.ReceiptDate).ThenByDescending(r => r.Code)
            .ToListAsync(cancellationToken);

    public void Add(PurchaseReceipt receipt) => db.PurchaseReceipts.Add(receipt);

    public void AddReturn(PurchaseReturn purchaseReturn) => db.PurchaseReturns.Add(purchaseReturn);

    public void RemoveLine(PurchaseReceiptLine line) => db.PurchaseReceiptLines.Remove(line);

    public void AddLine(PurchaseReceiptLine line) => db.PurchaseReceiptLines.Add(line);

    public void AddSeedLot(Domain.Entities.Cultivation.SeedReceiptLot lot) => db.SeedReceiptLots.Add(lot);

    public Task<PurchaseReturn?> GetReturnAsync(Guid returnId, CancellationToken cancellationToken) =>
        db.PurchaseReturns.AsNoTracking()
            .Include(r => r.Receipt)
            .Include(r => r.Lines).ThenInclude(l => l.ReceiptLine).ThenInclude(l => l.Item)
            .FirstOrDefaultAsync(r => r.Id == returnId, cancellationToken);

    // Lines, returns and payments are what the money figures are computed from.
    private static IQueryable<PurchaseReceipt> WithFigures(IQueryable<PurchaseReceipt> query) => query
        .Include(r => r.Lines).ThenInclude(l => l.Item)
        .Include(r => r.Returns).ThenInclude(rt => rt.Lines)
        .Include(r => r.Payments)
        .AsSplitQuery();
}
