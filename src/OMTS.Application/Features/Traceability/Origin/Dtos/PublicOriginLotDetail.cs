namespace OMTS.Application.Features.Traceability.Origin.Dtos;

/// <summary>The origin of a Published lot as the public page shows it, from the seed to the sale. No customer, staff or price (decided 10/11/2026).</summary>
/// <param name="ProductNames">The products of the lot on this invoice.</param>
/// <param name="SeedLots">Each seed lot hung on the lanes of the lot once.</param>
/// <param name="FirstSeedingDate">Null when the lot has no seeding session.</param>
/// <param name="CultivationLogCount">The cultivation logs of the cycle up to the harvest date.</param>
/// <param name="HarvestedKg">The estimated kg of the whole harvest lot.</param>
/// <param name="SoldOn">The invoice date.</param>
/// <param name="SoldKg">The kg of the lot on this invoice.</param>
/// <param name="WeighedCages">The cages of the lot weighed at the port for this invoice; 0 for a sale by the kg.</param>
public sealed record PublicOriginLotDetail(
    IReadOnlyList<string> ProductNames, string ZoneName, string RaftCode, DateOnly CycleStartDate, IReadOnlyList<PublicOriginSeedLotView> SeedLots, DateOnly? FirstSeedingDate,
    DateOnly? LastSeedingDate, int LaneCount, int CultivationLogCount, PublicOriginWaterView Water, DateOnly HarvestDate, int HarvestedCages, decimal HarvestedKg, DateOnly SoldOn,
    decimal SoldKg, int WeighedCages);
