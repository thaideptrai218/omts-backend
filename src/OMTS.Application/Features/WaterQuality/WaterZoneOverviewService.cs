using OMTS.Application.Common.Interfaces;
using OMTS.Application.Common.Interfaces.Persistence;
using OMTS.Application.Common.Messages;
using OMTS.Application.Features.Authorization;
using OMTS.Application.Features.Authorization.Models;
using OMTS.Application.Features.WaterQuality.Dtos;
using OMTS.Domain.Constants;
using OMTS.Domain.Entities.Cultivation;
using OMTS.Domain.Entities.Farming;

namespace OMTS.Application.Features.WaterQuality;

/// <summary>
/// WATER-03 step 1: the zones the caller can access, each with the time of its latest active measurement and whether it has
/// a water alert. The alert is not saved: it is worked out here each time, from that measurement and its own thresholds (BR-WATER-04).
/// </summary>
public sealed class WaterZoneOverviewService(
    IWaterMeasurementRepository measurements,
    ZoneAccessGuard zoneAccess,
    IStatusCatalog statusCatalog)
{
    public async Task<IReadOnlyList<WaterZoneOverviewItem>> ListAsync(bool inUseOnly, CancellationToken cancellationToken)
    {
        var permitted = await zoneAccess.PermittedZonesAsync(PermissionFunctions.WaterMeasurements, PermissionAction.View, cancellationToken);
        var zones = await measurements.ListZonesAsync(permitted, inUseOnly, track: false, cancellationToken);
        var latest = await measurements.LatestActiveByZoneAsync(cancellationToken);
        return zones.Select(zone => Item(zone, latest.GetValueOrDefault(zone.Id))).ToList();
    }

    private WaterZoneOverviewItem Item(FarmingZone zone, WaterMeasurement? latest)
    {
        IReadOnlyList<WaterValueOutside> outside = latest is not null && zone.StatusId == StatusIds.Usage.InUse
            ? WaterAlertService.Evaluate(latest).ValuesOutside
            : [];
        return new WaterZoneOverviewItem(
            zone.Id, zone.Code, zone.Name, statusCatalog.DisplayNameOf(zone.StatusId), statusCatalog.CodeOf(zone.StatusId),
            latest?.MeasuredAt, outside.Count > 0, outside);
    }
}
