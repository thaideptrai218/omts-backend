using OMTS.Application.Features.Authorization.Models;
using OMTS.Domain.Entities.Farming;

namespace OMTS.Application.Common.Models.Persistence;

/// <summary>A zone with the number of its In Use rafts, for the zone list (FARM-02).</summary>
public sealed record ZoneWithRaftCount(FarmingZone Zone, int InUseRaftCount);
