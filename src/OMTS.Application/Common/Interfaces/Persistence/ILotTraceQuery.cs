using OMTS.Application.Features.Authorization.Models;
using OMTS.Application.Features.Traceability.Tracing;
using OMTS.Domain.Entities.Harvest;

namespace OMTS.Application.Common.Interfaces.Persistence;

/// <summary>What Trace lot (TRACE-02) reads besides the lot and the seeding sessions of its cycle: the lots to choose from, the forward trace and the correction marks. Read only.</summary>
public interface ILotTraceQuery
{
    /// <summary>
    /// TRACE-02.0 step 1: the lots of the zones whose code starts with <paramref name="lotCode"/> and/or that are on the sales invoice <paramref name="invoiceCode"/> (not a cancelled one: its
    /// lines are no longer a sale of the lot), harvested within the day range, on a raft whose code starts with <paramref name="raftCode"/>, holding or selling a product whose name contains
    /// <paramref name="productName"/>, with their raft and zone: the lot of exactly that code first, then the latest harvest first, at most <paramref name="max"/>.
    /// </summary>
    Task<IReadOnlyList<HarvestLot>> SearchAsync(
        string? lotCode, string? invoiceCode, DateOnly? harvestFrom, DateOnly? harvestTo, string? raftCode, string? productName, PermittedZones zones, int max, CancellationToken cancellationToken);

    /// <summary>
    /// Reverse trace: the lots of the zones that grew from a seed lot (the seeding sessions of the lot's own cycle on its lanes, up to the harvest day, as the trace reads them) whose code is
    /// <paramref name="term"/> (receipt code with or without the line number) or whose supplier name contains it; the latest harvest first, at most <paramref name="max"/>.
    /// </summary>
    Task<IReadOnlyList<HarvestLot>> LotsOfSeedAsync(string term, PermittedZones zones, int max, CancellationToken cancellationToken);

    /// <summary>The invoice lines (not cancelled) of these lots, one row per lot and invoice with the kg of the lot. One query.</summary>
    Task<IReadOnlyList<SeedLotInvoiceRow>> InvoicesOfLotsAsync(IReadOnlyCollection<Guid> lotIds, CancellationToken cancellationToken);

    /// <summary>The harvest plan (with its sales orders) a lot came from and the claims still open on its invoices. Two small queries.</summary>
    Task<LotTraceContext> ContextAsync(Guid lotId, CancellationToken cancellationToken);

    /// <summary>The forward trace (BR-TRACE-03): every line of the lot on an invoice that is not cancelled, the oldest invoice first, then by line. One query whatever the number of invoices.</summary>
    Task<IReadOnlyList<LotSaleLine>> SaleLinesAsync(Guid lotId, CancellationToken cancellationToken);

    /// <summary>The sales returns that stand on the lines of the lot, per return and outcome (BR-TRACE-03). One query.</summary>
    Task<IReadOnlyList<LotSaleReturn>> SaleReturnsAsync(Guid lotId, CancellationToken cancellationToken);

    /// <summary>The TRACE-03 corrections of these seeding sessions and harvest lots, oldest first.</summary>
    Task<IReadOnlyList<TraceCorrectionRow>> CorrectionsAsync(IReadOnlyCollection<Guid> recordIds, CancellationToken cancellationToken);
}
