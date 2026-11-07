using OMTS.Application.Features.Authorization.Models;
using OMTS.Application.Features.Traceability.Tracing;
using OMTS.Domain.Entities.Harvest;

namespace OMTS.Application.Common.Interfaces.Persistence;

/// <summary>What Trace lot (TRACE-02) reads besides the lot and the seeding sessions of its cycle: the lots to choose from, the forward trace and the correction marks. Read only.</summary>
public interface ILotTraceQuery
{
    /// <summary>
    /// TRACE-02.0 step 1: the lots of the zones whose code starts with <paramref name="lotCode"/> and/or that are on the sales invoice <paramref name="invoiceCode"/> (a cancelled one
    /// too: it keeps its lines), with their raft and zone: the lot of exactly that code first, then the latest harvest first, at most <paramref name="max"/>.
    /// </summary>
    Task<IReadOnlyList<HarvestLot>> SearchAsync(string? lotCode, string? invoiceCode, PermittedZones zones, int max, CancellationToken cancellationToken);

    /// <summary>The forward trace (BR-TRACE-03): every line of the lot on an invoice that is not cancelled, the oldest invoice first, then by line. One query whatever the number of invoices.</summary>
    Task<IReadOnlyList<LotSaleLine>> SaleLinesAsync(Guid lotId, CancellationToken cancellationToken);

    /// <summary>The TRACE-03 corrections of these seeding sessions and harvest lots, oldest first.</summary>
    Task<IReadOnlyList<TraceCorrectionRow>> CorrectionsAsync(IReadOnlyCollection<Guid> recordIds, CancellationToken cancellationToken);
}
