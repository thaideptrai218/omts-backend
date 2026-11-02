using OMTS.Domain.Entities.Cultivation;
using OMTS.Domain.Entities.Farming;

namespace OMTS.Application.Features.WaterQuality.Dtos;

public sealed record ZoneWaterThresholds(Guid ZoneId, string ZoneCode, string ZoneName, string ZoneStatus, WaterThresholds Thresholds);
