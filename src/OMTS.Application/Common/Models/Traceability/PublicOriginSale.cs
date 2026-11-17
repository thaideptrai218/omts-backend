namespace OMTS.Application.Common.Models.Traceability;

/// <summary>The sales invoice behind a QR code, with only what the public provenance page may show (BR-ORIGIN-05): no customer, no staff, no price.</summary>
/// <param name="CooperativeName">The full name of the cooperative.</param>
/// <param name="Lines">In line order.</param>
/// <param name="WeighedCages">The cages of each lot on the port weighing of the invoice, by lot id; empty for a sale by the kg.</param>
public sealed record PublicOriginSale(string CooperativeName, DateOnly InvoiceDate, IReadOnlyList<PublicOriginSaleLine> Lines, IReadOnlyDictionary<Guid, int> WeighedCages);
