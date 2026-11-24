using Microsoft.EntityFrameworkCore;
using OMTS.Application.Common.Interfaces.Persistence;
using OMTS.Application.Features.Authorization.Models;
using OMTS.Application.Features.Traceability.Corrections;
using OMTS.Application.Features.Traceability.Tracing;
using OMTS.Domain.Constants;
using OMTS.Domain.Entities.Harvest;
using OMTS.Domain.Entities.Sales;

namespace OMTS.Infrastructure.Persistence.Queries;

/// <summary>TRACE-02 reads: the lot search, the invoice lines of a lot (forward trace) and the TRACE-03 audit rows (correction marks). Each is one query.</summary>
internal sealed class LotTraceQuery(OmtsDbContext db) : ILotTraceQuery
{
    public async Task<IReadOnlyList<HarvestLot>> SearchAsync(
        string? lotCode, string? invoiceCode, DateOnly? harvestFrom, DateOnly? harvestTo, string? raftCode, string? productName, PermittedZones zones, int max, CancellationToken cancellationToken)
    {
        var found = db.HarvestLots.AsNoTracking();
        if (lotCode is not null)
        {
            found = found.Where(l => l.LotCode.StartsWith(lotCode));
        }

        if (invoiceCode is not null)
        {
            // A cancelled invoice no longer sells the lot (the forward trace leaves it out), so searching by it would open a lot with an empty forward trace.
            found = found.Where(l => db.SalesInvoiceLines.Any(s => s.HarvestLotId == l.Id && s.Invoice.Code == invoiceCode && s.Invoice.StatusId != StatusIds.SalesInvoice.Cancelled));
        }

        if (harvestFrom is { } from)
        {
            found = found.Where(l => l.HarvestDate >= from);
        }

        if (harvestTo is { } to)
        {
            found = found.Where(l => l.HarvestDate <= to);
        }

        if (raftCode is not null)
        {
            found = found.Where(l => l.Raft.Code.StartsWith(raftCode));
        }

        if (productName is not null)
        {
            found = found.Where(l => l.ProductLines.Any(p => p.Product.Name.Contains(productName))
                || db.SalesInvoiceLines.Any(s => s.HarvestLotId == l.Id && s.Product.Name.Contains(productName)));
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

    public async Task<IReadOnlyList<HarvestLot>> LotsOfSeedAsync(string term, PermittedZones zones, int max, CancellationToken cancellationToken)
    {
        // PN000123-1 is a receipt line; PN000123 alone is every seed lot of the receipt; anything else is looked for in the supplier name.
        var (receiptCode, lineNumber) = SplitSeedLotCode(term);
        var found = db.HarvestLots.AsNoTracking().Where(l => db.HarvestLotLanes.Any(ll => ll.LotId == l.Id
            && db.SeedingRecordLanes.Any(s => s.LaneId == ll.LaneId
                // Only the sessions the trace itself reads for the lot: its own cycle, up to the harvest day.
                && s.SeedingRecord.CycleId == l.CycleId && s.SeedingRecord.SeedingDate <= l.HarvestDate
                && ((s.SeedingRecord.SeedLotLine.Receipt.Code == receiptCode && (lineNumber == null || s.SeedingRecord.SeedLotLine.LineNumber == lineNumber))
                    || s.SeedingRecord.SeedLotLine.Receipt.Supplier.Name.Contains(term)))));
        if (!zones.AllZones)
        {
            var zoneIds = zones.ZoneIds.ToList();
            found = found.Where(l => zoneIds.Contains(l.Raft.ZoneId));
        }

        return await found
            .Include(l => l.Raft).ThenInclude(r => r.Zone)
            .OrderByDescending(l => l.HarvestedAt).ThenBy(l => l.LotCode)
            .Take(max)
            .ToListAsync(cancellationToken);
    }

    private static (string ReceiptCode, int? LineNumber) SplitSeedLotCode(string term)
    {
        var dash = term.LastIndexOf('-');
        return dash > 0 && int.TryParse(term[(dash + 1)..], System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var line)
            ? (term[..dash], line)
            : (term, null);
    }

    public async Task<IReadOnlyList<SeedLotInvoiceRow>> InvoicesOfLotsAsync(IReadOnlyCollection<Guid> lotIds, CancellationToken cancellationToken)
    {
        var ids = lotIds.Distinct().ToList();
        if (ids.Count == 0)
        {
            return [];
        }

        var rows = await db.SalesInvoiceLines.AsNoTracking()
            .Where(l => ids.Contains(l.HarvestLotId) && l.Invoice.StatusId != StatusIds.SalesInvoice.Cancelled)
            .Select(l => new
            {
                l.HarvestLotId,
                l.InvoiceId,
                l.Invoice.Code,
                l.Invoice.InvoiceDate,
                l.Invoice.ZoneId,
                ZoneCode = l.Invoice.Zone.Code,
                CustomerName = l.Invoice.Customer == null ? null : l.Invoice.Customer.Name,
                CustomerPhone = l.Invoice.Customer == null ? null : l.Invoice.Customer.PhoneNumber,
                WalkIn = l.Invoice.CustomerId == null,
                l.Kg,
            })
            .ToListAsync(cancellationToken);
        return rows
            .GroupBy(r => (r.HarvestLotId, r.InvoiceId, r.Code, r.InvoiceDate, r.ZoneId, r.ZoneCode, r.CustomerName, r.CustomerPhone, r.WalkIn))
            .Select(g => new SeedLotInvoiceRow(g.Key.HarvestLotId, g.Key.InvoiceId, g.Key.Code, g.Key.InvoiceDate, g.Key.ZoneId, g.Key.ZoneCode, g.Key.CustomerName, g.Key.CustomerPhone, g.Key.WalkIn, g.Sum(r => r.Kg)))
            .OrderBy(r => r.InvoiceDate).ThenBy(r => r.InvoiceCode, StringComparer.Ordinal)
            .ToList();
    }

    public async Task<LotTraceContext> ContextAsync(Guid lotId, CancellationToken cancellationToken)
    {
        var plan = await db.HarvestLots.AsNoTracking()
            .Where(l => l.Id == lotId && l.HarvestPlanLine != null)
            .Select(l => new { l.HarvestPlanLine!.Plan.Id, l.HarvestPlanLine.Plan.Code, l.HarvestPlanLine.Plan.PlannedDate, l.HarvestPlanLine.Plan.StatusId })
            .FirstOrDefaultAsync(cancellationToken);
        var orders = plan is null
            ? []
            : await db.SalesOrderHarvestPlans.AsNoTracking().Where(o => o.PlanId == plan.Id).OrderBy(o => o.Order.Code)
                .Select(o => new TraceOrderRef(o.OrderId, o.Order.Code)).ToListAsync(cancellationToken);

        // Open: waiting for the Owner's response, or an offer the trader has not answered yet.
        var claims = await db.QualityClaims.AsNoTracking()
            .Where(c => (c.StatusId == StatusIds.QualityClaim.AwaitingResponse || c.StatusId == StatusIds.QualityClaim.OfferMade)
                && db.SalesInvoiceLines.Any(s => s.HarvestLotId == lotId && s.InvoiceId == c.InvoiceId))
            .OrderBy(c => c.SentAt)
            .Select(c => new TraceOpenClaim(c.Code, c.Invoice.Code, c.SentAt))
            .ToListAsync(cancellationToken);
        return new LotTraceContext(plan?.Code, plan?.PlannedDate, plan?.StatusId, orders, claims);
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
                // The price of a kg: a line counted in another unit (a cage) is priced per unit, so its kg price is the amount over its kg.
                l.Quantity == l.Kg ? l.UnitPrice : Math.Round(l.Amount / l.Kg, 0),
                l.Amount,
                l.Invoice.Customer == null ? null : l.Invoice.Customer.AccountId,
                l.Invoice.Returns.Any(r => r.StatusId == StatusIds.SalesReturn.Recorded)))
            .ToListAsync(cancellationToken);

    public async Task<IReadOnlyList<LotSaleReturn>> SaleReturnsAsync(Guid lotId, CancellationToken cancellationToken)
    {
        var rows = await db.SalesReturns.AsNoTracking().Where(SalesReturn.Standing).SelectMany(r => r.Lines.Where(l => l.HarvestLotId == lotId), (r, l) => new { r.InvoiceId, r.Id, r.Code, r.ReturnDate, l.ReturnedKg, l.Outcome })
            .ToListAsync(cancellationToken);
        return rows.GroupBy(r => (r.InvoiceId, r.Id, r.Code, r.ReturnDate, r.Outcome))
            .Select(g => new LotSaleReturn(g.Key.InvoiceId, g.Key.Id, g.Key.Code, g.Key.ReturnDate, g.Sum(r => r.ReturnedKg), g.Key.Outcome.ToString()))
            .OrderBy(r => r.Code, StringComparer.Ordinal)
            .ToList();
    }

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
