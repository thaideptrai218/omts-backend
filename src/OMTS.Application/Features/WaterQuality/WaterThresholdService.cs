using OMTS.Application.Common.Interfaces;
using OMTS.Application.Common.Interfaces.Persistence;
using OMTS.Application.Common.Results;
using OMTS.Application.Features.Audit;
using OMTS.Application.Features.Audit.Models;
using OMTS.Application.Features.Authorization;
using OMTS.Application.Features.Authorization.Models;
using OMTS.Application.Features.WaterQuality.Dtos;
using OMTS.Domain.Entities.Farming;

namespace OMTS.Application.Features.WaterQuality;

/// <summary>
/// ENV-04 – the Cooperative Owner sets the minimum and maximum of each water indicator per zone (or the same
/// set for every In Use zone at once). New thresholds apply only to measurements confirmed afterwards: each
/// confirmed measurement keeps the set it used, so existing alerts are never re-evaluated.
/// </summary>
public sealed class WaterThresholdService(
    IWaterMeasurementRepository measurements,
    ZoneAccessGuard zoneAccess,
    IStatusCatalog statusCatalog,
    AuditLogService audit,
    IUnitOfWork unitOfWork)
{
    public async Task<Result<ZoneWaterThresholds>> GetAsync(Guid zoneId, CancellationToken cancellationToken) =>
        await measurements.GetZoneAsync(zoneId, track: false, cancellationToken) is { } zone ? View(zone) : AppError.NotFound;

    /// <summary>The thresholds of every zone the caller may view (all zones for the Owner).</summary>
    public async Task<IReadOnlyList<ZoneWaterThresholds>> ListAsync(CancellationToken cancellationToken)
    {
        var permitted = await zoneAccess.PermittedZonesAsync(PermissionFunctions.WaterMeasurements, PermissionAction.View, cancellationToken);
        return (await measurements.ListZonesAsync(permitted, inUseOnly: false, track: false, cancellationToken)).Select(View).ToList();
    }

    public async Task<Result<ZoneWaterThresholds>> SetAsync(Guid zoneId, SetWaterThresholdsRequest request, CancellationToken cancellationToken)
    {
        var thresholds = WaterThresholdRules.Validate(request);
        if (!thresholds.IsSuccess)
        {
            return thresholds.Error!;
        }

        var zone = await measurements.GetZoneAsync(zoneId, track: true, cancellationToken);
        if (zone is null)
        {
            return AppError.NotFound;
        }

        Apply(zone, thresholds.Value!);
        await unitOfWork.SaveChangesAsync(cancellationToken);
        return View(zone);
    }

    /// <summary>The cooperative-wide default: the same thresholds for every In Use zone, one audit row per changed zone.</summary>
    public async Task<Result<IReadOnlyList<ZoneWaterThresholds>>> SetForAllInUseZonesAsync(SetWaterThresholdsRequest request, CancellationToken cancellationToken)
    {
        var thresholds = WaterThresholdRules.Validate(request);
        if (!thresholds.IsSuccess)
        {
            return thresholds.Error!;
        }

        var zones = await measurements.ListZonesAsync(PermittedZones.Everywhere, inUseOnly: true, track: true, cancellationToken);
        foreach (var zone in zones)
        {
            Apply(zone, thresholds.Value!);
        }

        await unitOfWork.SaveChangesAsync(cancellationToken);
        return zones.Select(View).ToList();
    }

    private void Apply(FarmingZone zone, WaterThresholds thresholds)
    {
        var before = WaterThresholds.Of(zone);
        if (before == thresholds)
        {
            return;
        }

        (zone.PhMin, zone.PhMax, zone.SalinityMin, zone.SalinityMax) = (thresholds.PhMin, thresholds.PhMax, thresholds.SalinityMin, thresholds.SalinityMax);
        audit.Record(new AuditEntry(WaterMeasurementAudit.ThresholdUpdated, nameof(FarmingZone))
        {
            RecordId = zone.Id,
            RecordCode = zone.Code,
            ZoneId = zone.Id,
            Before = before,
            After = thresholds,
        });
    }

    private ZoneWaterThresholds View(FarmingZone zone) =>
        new(zone.Id, zone.Code, zone.Name, statusCatalog.DisplayNameOf(zone.StatusId), WaterThresholds.Of(zone));
}
