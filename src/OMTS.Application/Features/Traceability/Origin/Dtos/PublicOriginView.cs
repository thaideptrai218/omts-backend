namespace OMTS.Application.Features.Traceability.Origin.Dtos;

/// <summary>The public provenance page of a QR code (BR-ORIGIN-01): the cooperative and the lots of the invoice that were published, in lot code order.</summary>
public sealed record PublicOriginView(string CooperativeName, IReadOnlyList<PublicOriginLotView> Lots);
