using Microsoft.EntityFrameworkCore;
using OMTS.Application.Common.Interfaces.Persistence;

namespace OMTS.Infrastructure.Persistence.Queries;

/// <summary>The links of a harvest lot to its seed lots, nursery batch and invoices: three small reads, whatever the size of the cycle.</summary>
internal sealed class HarvestLotReferenceQuery(OmtsDbContext db) : IHarvestLotReferenceQuery
{
    public async Task<LotOriginRows> OriginOfCycleAsync(Guid cycleId, Guid? nurseryBatchId, CancellationToken cancellationToken)
    {
        var batch = nurseryBatchId is { } batchId
            ? await db.NurseryBatches.AsNoTracking().Where(b => b.Id == batchId).Select(b => new { b.Id, b.Code, b.SeedLotLineId }).FirstOrDefaultAsync(cancellationToken)
            : null;
        var lineIds = await db.SeedingRecords.AsNoTracking().Where(s => s.CycleId == cycleId).Select(s => s.SeedLotLineId).Distinct().ToListAsync(cancellationToken);
        if (batch is not null && !lineIds.Contains(batch.SeedLotLineId))
        {
            lineIds.Add(batch.SeedLotLineId);
        }

        var seedLots = lineIds.Count == 0
            ? []
            : (await db.PurchaseReceiptLines.AsNoTracking().Where(l => lineIds.Contains(l.Id))
                .Select(l => new LotSeedLotRow(l.ReceiptId, l.Receipt.Code, l.LineNumber, l.Item == null ? string.Empty : l.Item.Code, l.Item == null ? string.Empty : l.Item.Name))
                .ToListAsync(cancellationToken))
            .OrderBy(l => l.ReceiptCode, StringComparer.Ordinal).ThenBy(l => l.LineNumber).ToList();
        return new LotOriginRows(seedLots, batch is null ? null : new LotNurseryBatchRow(batch.Id, batch.Code));
    }

    public async Task<IReadOnlyDictionary<Guid, LotInvoiceRow>> InvoicesAsync(IReadOnlyCollection<Guid> invoiceIds, CancellationToken cancellationToken)
    {
        var ids = invoiceIds.Distinct().ToList();
        if (ids.Count == 0)
        {
            return new Dictionary<Guid, LotInvoiceRow>();
        }

        return await db.SalesInvoices.AsNoTracking().Where(i => ids.Contains(i.Id))
            .Select(i => new LotInvoiceRow(i.Id, i.Code, i.SalesOrderId, i.SalesOrder == null ? null : i.SalesOrder.Code))
            .ToDictionaryAsync(i => i.Id, cancellationToken);
    }
}
