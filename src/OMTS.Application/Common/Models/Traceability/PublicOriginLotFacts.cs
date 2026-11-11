using OMTS.Domain.Enums;

namespace OMTS.Application.Common.Models.Traceability;

/// <summary>A lot of the invoice behind a QR code, with the origin facts the public page shows (BR-ORIGIN-05), worked out when asked.</summary>
/// <param name="LaneCount">The lanes of the lot.</param>
/// <param name="Seedings">The seeding sessions of the lot's cycle on its lanes, the earliest first.</param>
/// <param name="CultivationLogCount">The cultivation logs of the cycle up to the harvest date.</param>
public sealed record PublicOriginLotFacts(
    Guid Id, string LotCode, HarvestLotOriginState OriginState, DateTimeOffset? WithdrawnAt, string ZoneName, string RaftCode, DateOnly CycleStartDate, DateOnly HarvestDate,
    int CageCount, decimal EstimatedKg, int LaneCount, IReadOnlyList<PublicOriginSeedingRow> Seedings, int CultivationLogCount, PublicOriginWaterFacts Water);
