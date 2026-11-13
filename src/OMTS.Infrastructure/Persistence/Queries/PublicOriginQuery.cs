using Microsoft.EntityFrameworkCore;
using OMTS.Application.Common;
using OMTS.Application.Common.Interfaces.Persistence;
using OMTS.Application.Common.Models.Traceability;
using OMTS.Domain.Constants;

namespace OMTS.Infrastructure.Persistence.Queries;

/// <summary>The public provenance page reads: the invoice of a QR token, then the origin facts of its lots. An invoice has few lots, so each lot costs a few small queries.</summary>
internal sealed class PublicOriginQuery(OmtsDbContext db) : IPublicOriginQuery
{
    public async Task<PublicOriginSale?> SaleAsync(string qrToken, CancellationToken cancellationToken)
    {
        var invoice = await db.SalesInvoices.AsNoTracking()
            .Where(i => i.QrToken == qrToken && i.StatusId != StatusIds.SalesInvoice.Cancelled)
            .Select(i => new
            {
                i.InvoiceDate,
                i.PortWeighingId,
                Lines = i.Lines.OrderBy(l => l.LineNumber).Select(l => new PublicOriginSaleLine(l.HarvestLotId, l.Product.Name, l.Kg)).ToList(),
            })
            .FirstOrDefaultAsync(cancellationToken);
        if (invoice is null)
        {
            return null;
        }

        var cages = invoice.PortWeighingId is { } weighingId
            ? await db.PortWeighingCages.AsNoTracking()
                .Where(c => c.PortWeighingId == weighingId)
                .GroupBy(c => c.HarvestLotId)
                .Select(g => new { LotId = g.Key, Count = g.Count() })
                .ToDictionaryAsync(g => g.LotId, g => g.Count, cancellationToken)
            : [];
        var cooperative = await db.Cooperatives.AsNoTracking().OrderBy(c => c.CreatedAt).Select(c => c.FullName).FirstOrDefaultAsync(cancellationToken);
        return new PublicOriginSale(cooperative ?? string.Empty, invoice.InvoiceDate, invoice.Lines, cages);
    }

    public async Task<IReadOnlyList<PublicOriginLotFacts>> LotsAsync(IReadOnlyCollection<Guid> lotIds, CancellationToken cancellationToken)
    {
        var lots = await db.HarvestLots.AsNoTracking()
            .Include(l => l.Raft).ThenInclude(r => r.Zone)
            .Include(l => l.Cycle)
            .Include(l => l.Lanes)
            .Where(l => lotIds.Contains(l.Id))
            .OrderBy(l => l.LotCode)
            .ToListAsync(cancellationToken);

        var facts = new List<PublicOriginLotFacts>(lots.Count);
        foreach (var lot in lots)
        {
            var laneIds = lot.Lanes.Select(l => l.LaneId).ToList();
            var seedings = await db.SeedingRecords.AsNoTracking()
                .Where(s => s.CycleId == lot.CycleId && s.Lanes.Any(l => laneIds.Contains(l.LaneId)))
                .OrderBy(s => s.SeedingDate)
                .Select(s => new PublicOriginSeedingRow(
                    s.SeedingDate,
                    s.SeedLotLine.Item == null ? null : s.SeedLotLine.Item.Name,
                    s.SeedLotLine.SupplierLotCode,
                    s.SeedLotLine.Receipt.Supplier.Name,
                    s.SeedLotLine.Receipt.ReceiptDate))
                .ToListAsync(cancellationToken);
            var logs = await db.CultivationLogs.AsNoTracking().CountAsync(c => c.CycleId == lot.CycleId && c.VisitDate <= lot.HarvestDate, cancellationToken);

            // The business days of the cycle, from its start to the end of the harvest date (UTC+7).
            var from = BusinessClock.FromLocal(lot.Cycle.StartDate.ToDateTime(TimeOnly.MinValue));
            var to = BusinessClock.FromLocal(lot.HarvestDate.AddDays(1).ToDateTime(TimeOnly.MinValue));
            var water = await db.WaterMeasurements.AsNoTracking()
                .Where(w => w.ZoneId == lot.Raft.ZoneId && w.StatusId == StatusIds.WaterMeasurement.Active && w.MeasuredAt >= from && w.MeasuredAt < to)
                .GroupBy(w => 1)
                .Select(g => new PublicOriginWaterFacts(g.Count(), g.Average(w => w.Salinity), g.Average(w => w.Temperature), g.Average(w => w.Ph)))
                .FirstOrDefaultAsync(cancellationToken);

            facts.Add(new PublicOriginLotFacts(
                lot.Id, lot.LotCode, lot.OriginState, lot.WithdrawnAt, lot.Raft.Zone.Name, lot.Raft.Code, lot.Cycle.StartDate, lot.HarvestDate, lot.CageCount, lot.EstimatedKg,
                laneIds.Count, seedings, logs, water ?? new PublicOriginWaterFacts(0, null, null, null)));
        }

        return facts;
    }
}
