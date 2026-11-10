using OMTS.Application.Features.Traceability.Origin;

namespace OMTS.Application.Common.Interfaces.Persistence;

/// <summary>What the public provenance page (ORIGIN-01) reads: only the fields the page shows (BR-ORIGIN-01, NFR-03). Read only, one query each.</summary>
public interface IProvenancePageQuery
{
    /// <summary>The invoice whose QR code is exactly <paramref name="qrToken"/> (ordinal: the database may compare without case), or null.</summary>
    Task<ProvenanceInvoiceRow?> FindInvoiceAsync(string qrToken, CancellationToken cancellationToken);

    /// <summary>These lots with their raft, zone and photo keys.</summary>
    Task<IReadOnlyList<ProvenanceLotRow>> LotsAsync(IReadOnlyCollection<Guid> lotIds, CancellationToken cancellationToken);

    /// <summary>The seeding sessions of each lot's cycle on the lanes of the lot (BR-TRACE-03: a lane hung again shows each seed lot), one row per lot, date and seed item.</summary>
    Task<IReadOnlyList<ProvenanceSeedingRow>> SeedingsAsync(IReadOnlyCollection<Guid> lotIds, CancellationToken cancellationToken);
}
