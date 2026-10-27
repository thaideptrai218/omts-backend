using OMTS.Application.Common;
using OMTS.Application.Common.Concurrency;
using OMTS.Application.Common.Interfaces;
using OMTS.Application.Common.Interfaces.Persistence;
using OMTS.Application.Common.Messages;
using OMTS.Application.Common.Results;
using OMTS.Application.Features.AccessManagement.Dtos;
using OMTS.Application.Features.Audit;
using OMTS.Application.Features.Audit.Models;
using OMTS.Application.Features.Farming.Licenses;
using OMTS.Application.Features.Authorization.Models;
using OMTS.Domain.Constants;
using OMTS.Domain.Entities.Farming;

namespace OMTS.Application.Features.Farming.Zones;

/// <summary>
/// FARM-02 – farming zones. Writes are Cooperative Owner only (checked by the API filter); reads are
/// limited to the zones the caller may see (<see cref="FarmingLookupAccess"/>).
/// </summary>
public sealed class FarmingZoneService(
    IFarmingZoneRepository zones,
    IZoneLicenseRepository licenses,
    ICooperativeRepository cooperatives,
    FarmingLookupAccess lookupAccess,
    IStatusCatalog statusCatalog,
    RowVersionGuard rowVersions,
    StatusChangeService statusChanges,
    AuditLogService audit,
    IUnitOfWork unitOfWork,
    TimeProvider timeProvider)
{
    private const string RecordType = nameof(FarmingZone);
    private const int MaxPageSize = 100;

    public async Task<Paged<FarmingZoneListItem>> SearchAsync(string? search, int? statusId, int page, int pageSize, CancellationToken cancellationToken)
    {
        // The upper bound keeps (page - 1) * pageSize inside int.
        (page, pageSize) = (Math.Clamp(page, 1, int.MaxValue / MaxPageSize), Math.Clamp(pageSize, 1, MaxPageSize));
        var visible = await lookupAccess.VisibleZonesAsync(cancellationToken);
        var (items, total) = await zones.SearchAsync(search, statusId, visible, page, pageSize, cancellationToken);
        var badges = await BadgesAsync(items.Select(i => i.Zone.Id).ToList(), visible, cancellationToken);
        return new Paged<FarmingZoneListItem>(
            items.Select(i => new FarmingZoneListItem(
                i.Zone.Id, i.Zone.Code, i.Zone.Name, i.Zone.Location, i.Zone.Area, i.Zone.StatusId,
                statusCatalog.DisplayNameOf(i.Zone.StatusId), i.InUseRaftCount, badges.GetValueOrDefault(i.Zone.Id))).ToList(),
            page, pageSize, total);
    }

    public async Task<Result<FarmingZoneView>> GetAsync(Guid zoneId, CancellationToken cancellationToken)
    {
        var zone = await zones.GetForEditAsync(zoneId, cancellationToken);
        if (zone is null)
        {
            return AppError.NotFound;
        }

        return await lookupAccess.CanSeeZoneAsync(zoneId, cancellationToken) ? await ViewAsync(zone, cancellationToken) : AppError.Forbidden;
    }

    /// <summary>Picker for other modules: In Use zones unless <paramref name="includeDiscontinued"/>.</summary>
    public async Task<IReadOnlyList<ZoneLookupItem>> LookupAsync(bool includeDiscontinued, CancellationToken cancellationToken)
    {
        var visible = await lookupAccess.VisibleZonesAsync(cancellationToken);
        var found = await zones.ListAsync(includeDiscontinued, visible, cancellationToken);
        return found.Select(z => new ZoneLookupItem(z.Id, z.Code, z.Name, z.StatusId, statusCatalog.DisplayNameOf(z.StatusId))).ToList();
    }

    public async Task<IReadOnlyList<MapZone>> MapAsync(bool includeDiscontinued, CancellationToken cancellationToken)
    {
        var visible = await lookupAccess.VisibleZonesAsync(cancellationToken);
        var found = await zones.ListForMapAsync(includeDiscontinued, visible, cancellationToken);
        return found.Select(z => new MapZone(
            z.Id, z.Code, z.Name, z.Latitude, z.Longitude, z.StatusId, statusCatalog.DisplayNameOf(z.StatusId),
            z.Rafts.OrderBy(r => r.Code).Select(r => new MapRaft(r.Id, r.Code, r.Latitude, r.Longitude)).ToList())).ToList();
    }

    public async Task<Result<FarmingZoneView>> CreateAsync(SaveFarmingZoneRequest request, CancellationToken cancellationToken)
    {
        if (await cooperatives.GetCurrentAsync(cancellationToken) is not { } cooperative)
        {
            return AppError.NotFound;
        }

        var check = Validate(request, out var values);
        if (!check.IsValid)
        {
            return AppError.InvalidFields(check.Invalid);
        }

        if (await zones.FindByCodeAsync(values.Code, null, cancellationToken) is { } existing)
        {
            return Duplicate(existing.Id);
        }

        var zone = new FarmingZone { CooperativeId = cooperative.Id };
        Apply(zone, values);
        zones.Add(zone);
        audit.Record(new AuditEntry("FarmingZone.Created", RecordType) { RecordId = zone.Id, RecordCode = zone.Code, ZoneId = zone.Id, After = Snapshot(zone) });
        await unitOfWork.SaveChangesAsync(cancellationToken);
        return await ViewAsync(zone, cancellationToken);
    }

    public async Task<Result<FarmingZoneView>> UpdateAsync(Guid zoneId, SaveFarmingZoneRequest request, CancellationToken cancellationToken)
    {
        var zone = await zones.GetForEditAsync(zoneId, cancellationToken);
        if (zone is null)
        {
            return AppError.NotFound;
        }

        var check = Validate(request, out var values);
        if (!check.IsValid)
        {
            return AppError.InvalidFields(check.Invalid);
        }

        if (await zones.FindByCodeAsync(values.Code, zone.Id, cancellationToken) is { } existing)
        {
            return Duplicate(existing.Id);
        }

        if (await rowVersions.CheckAsync(zone, request.RowVersion, cancellationToken) is { } conflict)
        {
            return conflict;
        }

        var before = Snapshot(zone);
        Apply(zone, values);
        var (changedBefore, changedAfter) = AuditDiff.Changed(before, Snapshot(zone));
        if (changedAfter is not null)
        {
            // Old water measurements keep the measurement point stored on them; only the zone changes.
            audit.Record(new AuditEntry("FarmingZone.Updated", RecordType)
            {
                RecordId = zone.Id, RecordCode = zone.Code, ZoneId = zone.Id, Before = changedBefore, After = changedAfter,
            });
            await unitOfWork.SaveChangesAsync(cancellationToken);
        }

        return await ViewAsync(zone, cancellationToken);
    }

    /// <summary>In Use → Discontinued, blocked while the zone still has In Use rafts (MSG11).</summary>
    public async Task<Result<FarmingZoneView>> DiscontinueAsync(Guid zoneId, ChangeStatusRequest request, CancellationToken cancellationToken)
    {
        var zone = await zones.GetForEditAsync(zoneId, cancellationToken);
        if (zone is null)
        {
            return AppError.NotFound;
        }

        if (zone.StatusId != StatusIds.Usage.InUse)
        {
            return WrongStatus(zone, StatusIds.Usage.Discontinued, "the zone is already discontinued");
        }

        var rafts = await zones.InUseRaftCodesAsync(zone.Id, cancellationToken);
        if (rafts.Count > 0)
        {
            return AppError.Conflict(UserMessages.StillInUse(rafts.Count, string.Join(", ", rafts)), new Dictionary<string, object?> { ["raftCodes"] = rafts });
        }

        return await ChangeStatusAsync(zone, StatusIds.Usage.Discontinued, "FarmingZone.Discontinued", request, cancellationToken);
    }

    /// <summary>Discontinued → In Use, always allowed.</summary>
    public async Task<Result<FarmingZoneView>> ReactivateAsync(Guid zoneId, ChangeStatusRequest request, CancellationToken cancellationToken)
    {
        var zone = await zones.GetForEditAsync(zoneId, cancellationToken);
        if (zone is null)
        {
            return AppError.NotFound;
        }

        return zone.StatusId == StatusIds.Usage.Discontinued
            ? await ChangeStatusAsync(zone, StatusIds.Usage.InUse, "FarmingZone.Reactivated", request, cancellationToken)
            : WrongStatus(zone, StatusIds.Usage.InUse, "the zone is already in use");
    }

    private async Task<Result<FarmingZoneView>> ChangeStatusAsync(
        FarmingZone zone, int newStatusId, string action, ChangeStatusRequest request, CancellationToken cancellationToken)
    {
        var entry = new AuditEntry(action, RecordType) { RecordId = zone.Id, RecordCode = zone.Code, ZoneId = zone.Id };
        return await statusChanges.ChangeAsync(zone, newStatusId, entry, request.Reason, request.RowVersion, cancellationToken) is { } error
            ? error
            : await ViewAsync(zone, cancellationToken);
    }

    private AppError WrongStatus(FarmingZone zone, int newStatusId, string reason) => statusChanges.WrongStatus("farming zone", zone, newStatusId, reason);

    private static AppError Duplicate(Guid existingId) => new(
        ErrorKind.Conflict,
        UserMessages.AlreadyExists("Farming zone"),
        new Dictionary<string, object?> { ["existingId"] = existingId, ["existingZoneId"] = existingId });

    private async Task<Dictionary<Guid, LicenseState?>> BadgesAsync(IReadOnlyCollection<Guid> zoneIds, PermittedZones visible, CancellationToken cancellationToken)
    {
        // Licenses are Owner-only (FARM-03); only the Owner, who sees every zone, gets the badge.
        if (!visible.AllZones || zoneIds.Count == 0 || !await lookupAccess.IsCooperativeOwnerAsync(cancellationToken))
        {
            return [];
        }

        var today = timeProvider.Today();
        var covering = await licenses.ListCoveringAsync(zoneIds, cancellationToken);
        return zoneIds.ToDictionary(id => id, id => ZoneLicenseOverview.WorstFor(id, covering, today));
    }

    private async Task<FarmingZoneView> ViewAsync(FarmingZone zone, CancellationToken cancellationToken)
    {
        var badges = await BadgesAsync([zone.Id], await lookupAccess.VisibleZonesAsync(cancellationToken), cancellationToken);
        var rafts = await zones.InUseRaftCodesAsync(zone.Id, cancellationToken);
        return new FarmingZoneView(
            zone.Id, zone.Code, zone.Name, zone.Location, zone.Latitude, zone.Longitude, zone.Area, zone.WaterMeasurementPoint,
            zone.StatusId, statusCatalog.DisplayNameOf(zone.StatusId), rafts.Count, badges.GetValueOrDefault(zone.Id),
            RowVersionGuard.Encode(zone.RowVersion));
    }

    private static FieldCheck Validate(SaveFarmingZoneRequest request, out ZoneValues values)
    {
        var check = new FieldCheck();
        var code = check.Required(request.Code, "Code", 50);
        var name = check.Required(request.Name, "Name", 200);
        var location = check.Required(request.Location, "Location", 500);
        check.Coordinates(request.Latitude, request.Longitude, required: true);
        check.Check(request.Area is > 0 and < 100_000_000_000_000m, "Area");
        var point = check.Required(request.WaterMeasurementPoint, "Water measurement point", 200);
        values = new ZoneValues(code, name, location, request.Latitude, request.Longitude, request.Area, point);
        return check;
    }

    private static void Apply(FarmingZone zone, ZoneValues values)
    {
        zone.Code = values.Code;
        zone.Name = values.Name;
        zone.Location = values.Location;
        zone.Latitude = values.Latitude;
        zone.Longitude = values.Longitude;
        zone.Area = values.Area;
        zone.WaterMeasurementPoint = values.WaterMeasurementPoint;
    }

    private static object Snapshot(FarmingZone z) => new { z.Code, z.Name, z.Location, z.Latitude, z.Longitude, z.Area, z.WaterMeasurementPoint };

    private sealed record ZoneValues(
        string Code, string Name, string Location, decimal? Latitude, decimal? Longitude, decimal? Area, string WaterMeasurementPoint);
}
