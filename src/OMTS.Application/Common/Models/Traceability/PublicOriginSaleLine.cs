namespace OMTS.Application.Common.Models.Traceability;

/// <summary>A line of the invoice behind a QR code: the lot, the product and the kg, never the price.</summary>
public sealed record PublicOriginSaleLine(Guid HarvestLotId, string ProductName, decimal Kg);
