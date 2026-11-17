namespace OMTS.Application.Common.Models.Traceability;

/// <summary>The sales invoice behind a QR code, with only what the public provenance page may show (BR-ORIGIN-05): no customer, no staff, no price.</summary>
/// <param name="CooperativeName">The full name of the cooperative.</param>
/// <param name="Lines">In line order.</param>
public sealed record PublicOriginSale(string CooperativeName, DateOnly InvoiceDate, IReadOnlyList<PublicOriginSaleLine> Lines);
