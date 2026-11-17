using Microsoft.EntityFrameworkCore;
using OMTS.Application.Common.Interfaces.Persistence;
using OMTS.Application.Features.Traceability.Origin;
using OMTS.Domain.Constants;
using OMTS.Domain.Enums;

namespace OMTS.Infrastructure.Persistence.Queries;

/// <summary>
/// ORIGIN-01 reads. Each projects only the fields the page shows (BR-ORIGIN-01: the customer, prices and kg are never read), in one query whatever the number of lots. The lines left out are the
/// lots this invoice's sales return threw away (BR-ORIGIN-05, CLAIM-03); a cancelled return lists them again, as the filter asks for one that stands.
/// </summary>
internal sealed class ProvenancePageQuery(OmtsDbContext db) : IProvenancePageQuery
{
    public async Task<ProvenanceInvoiceRow?> FindInvoiceAsync(string qrToken, CancellationToken cancellationToken)
    {
        var found = await db.SalesInvoices.AsNoTracking()
            .Where(i => i.QrToken == qrToken)
            .Select(i => new
            {
                i.QrToken,
                Row = new ProvenanceInvoiceRow(
                    i.Code,
                    i.InvoiceDate,
                    i.StatusId == StatusIds.SalesInvoice.Cancelled,
                    i.Lines
                        .Where(l => !i.Returns.Any(r => r.StatusId == StatusIds.SalesReturn.Recorded && r.Lines.Any(d => d.Outcome == SalesReturnOutcome.ThrownAway && d.HarvestLotId == l.HarvestLotId)))
                        .OrderBy(l => l.LineNumber).Select(l => new ProvenanceInvoiceLineRow(l.LineNumber, l.HarvestLotId, l.Product.Name)).ToList()),
            })
            .FirstOrDefaultAsync(cancellationToken);

        // SQL Server compares without case: only the exact token opens the page, so the 128 bits are not cut down (BR-TRACE-02).
        return found is not null && string.Equals(found.QrToken, qrToken, StringComparison.Ordinal) ? found.Row : null;
    }

    public async Task<IReadOnlyList<ProvenanceLotRow>> LotsAsync(IReadOnlyCollection<Guid> lotIds, CancellationToken cancellationToken)
    {
        var ids = lotIds.Distinct().ToList();
        return ids.Count == 0
            ? []
            : await db.HarvestLots.AsNoTracking().AsSplitQuery()
                .Where(l => ids.Contains(l.Id))
                .Select(l => new ProvenanceLotRow(
                    l.Id,
                    l.LotCode,
                    l.HarvestDate,
                    l.PublishedAt,
                    l.WithdrawnAt,
                    l.Raft.Code,
                    l.Raft.Latitude,
                    l.Raft.Longitude,
                    l.Raft.Zone.Name,
                    l.Raft.Zone.Latitude,
                    l.Raft.Zone.Longitude,
                    l.Photos.OrderBy(p => p.SortOrder).Select(p => p.FileKey).ToList(),
                    l.Raft.Photos.OrderBy(p => p.SortOrder).Select(p => p.FileKey).ToList()))
                .ToListAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<ProvenanceSeedingRow>> SeedingsAsync(IReadOnlyCollection<Guid> lotIds, CancellationToken cancellationToken)
    {
        var ids = lotIds.Distinct().ToList();
        if (ids.Count == 0)
        {
            return [];
        }

        var rows = await (
                from lane in db.HarvestLotLanes.AsNoTracking()
                where ids.Contains(lane.LotId)
                from seeded in db.SeedingRecordLanes.AsNoTracking()
                where seeded.LaneId == lane.LaneId && seeded.SeedingRecord.CycleId == lane.Lot.CycleId
                select new
                {
                    lane.LotId,
                    seeded.SeedingRecord.SeedingDate,
                    ItemName = seeded.SeedingRecord.SeedLotLine.Item == null ? string.Empty : seeded.SeedingRecord.SeedLotLine.Item.Name,
                })
            .Distinct()
            .ToListAsync(cancellationToken);
        return rows.Select(r => new ProvenanceSeedingRow(r.LotId, r.SeedingDate, r.ItemName)).ToList();
    }
}
