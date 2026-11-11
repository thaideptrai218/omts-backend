namespace OMTS.Application.Common.Models.Traceability;

/// <summary>A seeding session on a lane of the lot, with the seed lot it hung and the receipt that brought it (GBR-04).</summary>
/// <param name="ItemName">The seed item; null when the receipt line has none.</param>
public sealed record PublicOriginSeedingRow(DateOnly SeedingDate, string? ItemName, string? SupplierLotCode, string SupplierName, DateOnly ReceiptDate);
