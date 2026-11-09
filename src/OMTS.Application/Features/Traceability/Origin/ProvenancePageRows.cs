using OMTS.Domain.Entities.Harvest;
using OMTS.Domain.Enums;

namespace OMTS.Application.Features.Traceability.Origin;

/// <summary>The invoice of a QR code as the provenance page reads it (ORIGIN-01.0 step 1): its latest lines, nothing about the customer or the money.</summary>
/// <param name="Lines">By line number; a corrected invoice holds its corrected lines (BR-ORIGIN-05).</param>
public sealed record ProvenanceInvoiceRow(string Code, DateOnly InvoiceDate, bool IsCancelled, IReadOnlyList<ProvenanceInvoiceLineRow> Lines);

/// <summary>One line of the invoice: the lot and the product sold from it (no kg, no price: BR-ORIGIN-04).</summary>
public sealed record ProvenanceInvoiceLineRow(int LineNumber, Guid HarvestLotId, string ProductName);

/// <summary>A lot of the invoice with what its origin information shows (BR-ORIGIN-03); photo keys are signed by the service.</summary>
/// <param name="HarvestPhotoKeys">The harvest photos, by slot.</param>
/// <param name="RaftPhotoKeys">The current photos of the lot's raft, by slot.</param>
public sealed record ProvenanceLotRow(
    Guid Id,
    string LotCode,
    DateOnly HarvestDate,
    DateTimeOffset? PublishedAt,
    DateTimeOffset? WithdrawnAt,
    string RaftCode,
    decimal? RaftLatitude,
    decimal? RaftLongitude,
    string ZoneName,
    decimal? ZoneLatitude,
    decimal? ZoneLongitude,
    IReadOnlyList<string> HarvestPhotoKeys,
    IReadOnlyList<string> RaftPhotoKeys)
{
    public HarvestLotOriginState OriginState => HarvestLot.OriginStateOf(PublishedAt, WithdrawnAt);
}

/// <summary>A seeding session of the lot's cycle on a lane of the lot: its date and the seed item of its seed lot, never the supplier or the quantity (BR-ORIGIN-03/04).</summary>
public sealed record ProvenanceSeedingRow(Guid LotId, DateOnly SeedingDate, string SeedItemName);
