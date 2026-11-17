namespace OMTS.Application.Features.Traceability.Origin.Dtos;

/// <summary>A seed lot of the lot: the seed, the supplier's lot code, the supplier and the day it was received.</summary>
public sealed record PublicOriginSeedLotView(string? ItemName, string? SupplierLotCode, string SupplierName, DateOnly ReceivedOn);
