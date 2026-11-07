using Microsoft.EntityFrameworkCore;
using OMTS.Application.Common.Interfaces.Persistence;
using OMTS.Application.Features.Authorization.Models;
using OMTS.Application.Features.Traceability.Corrections;
using OMTS.Application.Features.Traceability.Tracing;
using OMTS.Domain.Constants;
using OMTS.Domain.Entities.Harvest;

namespace OMTS.Infrastructure.Persistence.Queries;

/// <summary>TRACE-02 reads: the lot search, the invoice lines of a lot (forward trace) and the TRACE-03 audit rows (correction marks). Each is one query.</summary>
internal sealed class LotTraceQuery(OmtsDbContext db) : ILotTraceQuery
{
    public async Task<IReadOnlyList<HarvestLot>> SearchAsync(string? lotCode, string? invoiceCode, PermittedZones zones, int max, CancellationToken cancellationToken)
    {
        var found = db.HarvestLots.AsNoTracking();
        if (lotCode is not null)
        {
            found = found.Where(l => l.LotCode.StartsWith(lotCode));
        }

        if (invoiceCode is not null)
        {
            found = found.Where(l => db.SalesInvoiceLines.Any(s => s.HarvestLotId == l.Id && s.Invoice.Code == invoiceCode));
        }

        if (!zones.AllZones)
        {
            var zoneIds = zones.ZoneIds.ToList();
            found = found.Where(l => zoneIds.Contains(l.Raft.ZoneId));
        }

        // The lot with exactly this code first (a code with a suffix, -2, starts with the code of the first lot of that day).
        return await found
            .Include(l => l.Raft).ThenInclude(r => r.Zone)
            .OrderByDescending(l => l.LotCode == lotCode).ThenByDescending(l => l.HarvestedAt).ThenBy(l => l.LotCode)
            .Take(max)
            .ToListAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<LotSaleLine>> SaleLinesAsync(Guid lotId, CancellationToken cancellationToken) =>
        await db.SalesInvoiceLines.AsNoTracking()
            .Where(l => l.HarvestLotId == lotId && l.Invoice.StatusId != StatusIds.SalesInvoice.Cancelled)
            .OrderBy(l => l.Invoice.InvoicedAt).ThenBy(l => l.Invoice.Code).ThenBy(l => l.LineNumber)
            .Select(l => new LotSaleLine(
                l.InvoiceId,
                l.Invoice.Code,
                l.Invoice.InvoicedAt,
                l.Invoice.InvoiceDate,
                l.Invoice.ZoneId,
                l.Invoice.Zone.Code,
                l.Invoice.CustomerId,
                l.Invoice.Customer == null ? null : l.Invoice.Customer.Name,
                l.Invoice.Customer == null ? null : l.Invoice.Customer.PhoneNumber,
                l.LineNumber,
                l.Product.Name,
                l.Kg,
                l.UnitPrice,
                l.Amount))
            .ToListAsync(cancellationToken);

    public async Task<IReadOnlyList<TraceCorrectionRow>> CorrectionsAsync(IReadOnlyCollection<Guid> recordIds, CancellationToken cancellationToken)
    {
        var ids = recordIds.Distinct().ToList();
        return ids.Count == 0
            ? []
            : await db.AuditEvents.AsNoTracking()
                .Where(e => (e.Action == TraceCorrectionAudit.SeedingRecordCorrected || e.Action == TraceCorrectionAudit.HarvestLotCorrected)
                    && e.RecordId != null && ids.Contains(e.RecordId.Value))
                .OrderBy(e => e.OccurredAt)
                .Select(e => new TraceCorrectionRow(e.RecordId!.Value, e.OccurredAt, e.ActorAccountId, e.Reason))
                .ToListAsync(cancellationToken);
    }
}
