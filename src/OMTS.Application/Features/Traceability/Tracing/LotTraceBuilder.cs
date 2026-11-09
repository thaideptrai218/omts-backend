using OMTS.Application.Common.Interfaces;
using OMTS.Application.Common.Messages;
using OMTS.Application.Features.Authorization;
using OMTS.Application.Features.Authorization.Models;
using OMTS.Application.Features.Cultivation.Seeding;
using OMTS.Application.Features.Traceability.Publishing;
using OMTS.Domain.Constants;
using OMTS.Domain.Entities.Cultivation;
using OMTS.Domain.Entities.Harvest;
using OMTS.Domain.Enums;

namespace OMTS.Application.Features.Traceability.Tracing;

/// <summary>What one trace is built from, read beforehand (no query here).</summary>
/// <param name="Lot">With its lanes, cycle, raft and zone.</param>
/// <param name="CycleSessions">Every seeding session of the lot's cycle, with lanes, seed lot, receipt, supplier and item.</param>
/// <param name="Corrections">The TRACE-03 corrections of the lot and of its sessions.</param>
/// <param name="ActorNames">The display names of the people who corrected them.</param>
public sealed record LotTraceSource(
    HarvestLot Lot,
    IReadOnlyList<SeedingRecord> CycleSessions,
    IReadOnlyList<LotSaleLine> SaleLines,
    IReadOnlyList<TraceCorrectionRow> Corrections,
    IReadOnlyDictionary<Guid, string> ActorNames);

/// <summary>
/// Builds the trace of a harvest lot (TRACE-02): backward from the lanes of the lot to every seeding session of the lot's cycle on these lanes, their seed lots, receipts and suppliers
/// (BR-TRACE-03); what is missing, named and never filled in (BR-TRACE-01, MSG-TRACE-02); forward to the invoice lines grouped by invoice, with the traders; the marks of TRACE-03.
/// </summary>
public static class LotTraceBuilder
{
    public static LotTrace Build(LotTraceSource source, AccountAccess? access, IStatusCatalog statusCatalog)
    {
        var lot = source.Lot;
        var zoneId = lot.Raft.ZoneId;
        var marks = source.Corrections.ToLookup(c => c.RecordId, c => new TraceCorrectionMark(
            c.CorrectedAt, c.ActorAccountId, c.ActorAccountId is { } id ? source.ActorNames.GetValueOrDefault(id) : null, c.Reason));
        var lanes = lot.Lanes
            .OrderBy(l => l.Lane.LaneNumber)
            .Select(l => new TraceLane(
                l.Lane.LaneNumber,
                l.IsFinished,
                source.CycleSessions
                    .Where(s => s.Lanes.Any(sl => sl.LaneId == l.LaneId))
                    .OrderBy(s => s.SeedingDate).ThenBy(s => s.CreatedAt)
                    .Select(s => Seeding(s, marks[s.Id].ToList(), statusCatalog))
                    .ToList()))
            .ToList();
        var missing = ProvenanceReadiness.MissingInTrace(lanes.SelectMany(LaneRows(lot.Id)));
        var message = missing.Count == 0 ? null : UserMessages.TraceIncomplete(lot.LotCode, missing);
        var invoices = Invoices(source.SaleLines, access);

        return new LotTrace(
            lot.Id,
            lot.LotCode,
            lot.HarvestedAt,
            lot.HarvestDate,
            lot.CageCount,
            lot.EstimatedKg,
            lot.StatusId,
            statusCatalog.DisplayNameOf(lot.StatusId),
            lot.OriginState,
            lot.PublishedAt,
            lot.WithdrawnAt,
            marks[lot.Id].ToList(),
            lot.RaftId,
            lot.Raft.Code,
            zoneId,
            lot.Raft.Zone.Code,
            lot.Raft.Zone.Name,
            lot.CycleId,
            lot.Cycle.StartDate,
            lot.Cycle.CloseDate,
            lot.Cycle.StatusId,
            statusCatalog.DisplayNameOf(lot.Cycle.StatusId),
            lanes,
            missing,
            message?.Text,
            message?.Code,
            invoices,
            invoices.Sum(i => i.Kg),
            LotPublishRules.Block(access, lot, zoneId, onSalesInvoice: invoices.Count > 0) is null,
            Allows(access, PermissionFunctions.Provenance, PermissionAction.Edit, zoneId),
            Allows(access, PermissionFunctions.LotTracing, PermissionAction.Export, zoneId),
            access?.IsCooperativeOwner == true && lot.OriginState == HarvestLotOriginState.Published);
    }

    public static bool Allows(AccountAccess? access, string function, PermissionAction action, Guid zoneId) =>
        access is not null && access.Allows(function, action, PermissionScope.ZoneScoped, zoneId);

    private static TraceSeeding Seeding(SeedingRecord session, IReadOnlyList<TraceCorrectionMark> marks, IStatusCatalog statusCatalog)
    {
        var receipt = session.SeedLotLine.Receipt;
        return new TraceSeeding(
            session.Id, session.SeedingDate, session.Quantity, SeedingViews.LotRef(session.SeedLotLine), receipt.StatusId, statusCatalog.DisplayNameOf(receipt.StatusId),
            receipt.StatusId == StatusIds.PurchaseReceipt.Completed, marks);
    }

    // The rows BR-TRACE-01 is checked on, as ProvenanceReadiness reads them: a lane with no session is one row with no seed lot.
    private static Func<TraceLane, IEnumerable<LotLaneSeedLot>> LaneRows(Guid lotId) => lane => lane.Seedings.Count == 0
        ? [new LotLaneSeedLot(lotId, lane.LaneNumber, null, false)]
        : lane.Seedings.Select(s => new LotLaneSeedLot(lotId, lane.LaneNumber, s.SeedLot.Code, s.OnCompletedReceipt));

    // The lines of one invoice together, in the order read. Prices and amounts only for whoever views the sales invoices of the invoice's zone (BR-SALE-08: it may not be the lot's).
    private static List<TraceInvoice> Invoices(IReadOnlyList<LotSaleLine> lines, AccountAccess? access) => lines
        .GroupBy(l => l.InvoiceId)
        .Select(invoice =>
        {
            var first = invoice.First();
            var prices = Allows(access, PermissionFunctions.SalesInvoices, PermissionAction.View, first.InvoiceZoneId);
            var traceLines = invoice
                .Select(l => new TraceInvoiceLine(l.LineNumber, l.ProductName, l.Kg, prices ? l.UnitPrice : null, prices ? l.Amount : null))
                .ToList();

            // CLAIM-03 seam: the sales returns of the invoice, not cancelled, go here when CLAIM-03 is built (BR-TRACE-03, AC-11).
            return new TraceInvoice(
                first.InvoiceId, first.InvoiceCode, first.InvoicedAt, first.InvoiceDate, first.InvoiceZoneId, first.InvoiceZoneCode, first.CustomerId, first.CustomerName,
                first.CustomerPhone, first.CustomerId is null, traceLines.Sum(l => l.Kg), traceLines, []);
        })
        .ToList();
}
