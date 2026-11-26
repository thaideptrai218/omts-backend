using System.Text.Json.Nodes;
using OMTS.Application.Common;
using OMTS.Application.Common.Concurrency;
using OMTS.Application.Common.Exceptions;
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
/// limited to the zones the caller may see (<see cref="FarmingLookupAccess"/>). A Discontinued zone is read-only until
/// it is resumed (GBR-01, MSG-GEN-09).
/// </summary>
public sealed class FarmingZoneService(
    IFarmingZoneRepository zones,
    IRaftRepository rafts,
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
        var raftZones = await lookupAccess.VisibleRaftZonesAsync(cancellationToken);
        var found = await zones.ListForMapAsync(includeDiscontinued, visible, cancellationToken);
        return found.Select(z => new MapZone(
            z.Id, z.Code, z.Name, z.Latitude, z.Longitude, ZoneBoundaryJson.Read(z.BoundaryGeoJson), z.StatusId, statusCatalog.DisplayNameOf(z.StatusId),
            raftZones.Allows(z.Id) ? z.Rafts.OrderBy(r => r.Code).Select(r => new MapRaft(r.Id, r.Code, r.Latitude, r.Longitude)).ToList() : [])).ToList();
    }

    /// <summary>The proposed code of a new zone (KV01, KV02…; GBR-03); the Owner may replace it.</summary>
    public async Task<FarmingZoneCodeProposal> ProposeCodeAsync(CancellationToken cancellationToken) => new(
        SequentialCodes.Next(
            SequentialCodes.ZonePrefix, SequentialCodes.ZoneDigits, SequentialCodes.ZoneCodeMaxLength,
            await zones.CodesStartingWithAsync(SequentialCodes.ZonePrefix, cancellationToken)));

    public async Task<Result<FarmingZoneSaved>> CreateAsync(SaveFarmingZoneRequest request, CancellationToken cancellationToken)
    {
        if (await cooperatives.GetCurrentAsync(cancellationToken) is not { } cooperative)
        {
            return AppError.NotFound;
        }

        var check = Validate(request, out var values);
        if (!check.IsValid)
        {
            return check.ToError();
        }

        if (await zones.FindByCodeAsync(values.Code, null, cancellationToken) is { } existing)
        {
            return Duplicate(existing.Id);
        }

        var zone = new FarmingZone { CooperativeId = cooperative.Id };
        Apply(zone, values, keepPin: false);
        zones.Add(zone);
        audit.Record(new AuditEntry("FarmingZone.Created", RecordType) { RecordId = zone.Id, RecordCode = zone.Code, ZoneId = zone.Id, After = Snapshot(zone) });
        if (await SaveAsync(zone, exceptZoneId: null, cancellationToken) is { } duplicate)
        {
            return duplicate;
        }

        return await SavedAsync(zone, cancellationToken);
    }

    /// <summary>FARM-02.1: only an In Use zone is edited (FARM-02.1.E1, MSG-GEN-09); the codes of its rafts never change.</summary>
    public async Task<Result<FarmingZoneSaved>> UpdateAsync(Guid zoneId, SaveFarmingZoneRequest request, CancellationToken cancellationToken)
    {
        var zone = await zones.GetForEditAsync(zoneId, cancellationToken);
        if (zone is null)
        {
            return AppError.NotFound;
        }

        if (zone.StatusId != StatusIds.Usage.InUse)
        {
            return WrongStatus(zone);
        }

        var check = Validate(request, out var values, ignorePin: zone.BoundaryGeoJson is not null);
        if (!check.IsValid)
        {
            return check.ToError();
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
        // Once the zone has a boundary its pin is the boundary's centroid, set by the server: the edit never moves or wipes it.
        Apply(zone, values, keepPin: zone.BoundaryGeoJson is not null);
        var (changedBefore, changedAfter) = AuditDiff.Changed(before, Snapshot(zone));
        if (changedAfter is not null)
        {
            // Only the zone changes: a water measurement points to it and keeps no copy of these values.
            audit.Record(new AuditEntry("FarmingZone.Updated", RecordType)
            {
                RecordId = zone.Id, RecordCode = zone.Code, ZoneId = zone.Id, Before = changedBefore, After = changedAfter,
            });
            if (await SaveAsync(zone, zone.Id, cancellationToken) is { } duplicate)
            {
                return duplicate;
            }
        }

        return await SavedAsync(zone, cancellationToken);
    }

    /// <summary>
    /// Saves the licensed sea area the app read from the photographed map, or clears it with a null boundary. Like an edit,
    /// only for an In Use zone (MSG-GEN-09) and against the version the client read (MSG06). In Use rafts pinned outside the
    /// new boundary are named in a warning: PM decision 25/11/2026 (warn, never block).
    /// </summary>
    public async Task<Result<FarmingZoneSaved>> SetBoundaryAsync(Guid zoneId, SaveZoneBoundaryRequest request, CancellationToken cancellationToken)
    {
        var zone = await zones.GetForEditAsync(zoneId, cancellationToken);
        if (zone is null)
        {
            return AppError.NotFound;
        }

        if (zone.StatusId != StatusIds.Usage.InUse)
        {
            return WrongStatus(zone);
        }

        var check = new FieldCheck();
        var boundary = ZoneBoundaryInput.Read(request.Boundary, check);
        if (!check.IsValid)
        {
            return check.ToError();
        }

        if (await rowVersions.CheckAsync(zone, request.RowVersion, cancellationToken) is { } conflict)
        {
            return conflict;
        }

        var before = BoundarySnapshot(zone);
        zone.BoundaryGeoJson = ZoneBoundaryJson.Write(boundary);
        if (ZoneBoundaryRules.Centroid(boundary) is { } pin)
        {
            // The pin of a zone with a boundary is its centroid; clearing the boundary keeps the last pin.
            (zone.Latitude, zone.Longitude) = (pin.Latitude, pin.Longitude);
        }

        var (changedBefore, changedAfter) = AuditDiff.Changed(before, BoundarySnapshot(zone));
        if (changedAfter is not null)
        {
            audit.Record(new AuditEntry("FarmingZone.BoundaryChanged", RecordType)
            {
                RecordId = zone.Id, RecordCode = zone.Code, ZoneId = zone.Id, Before = changedBefore, After = changedAfter,
            });
            await unitOfWork.SaveChangesAsync(cancellationToken);
        }

        var inUseRafts = boundary is null ? [] : await rafts.ListByZoneAsync(zone.Id, StatusIds.Usage.InUse, cancellationToken);
        return await SavedAsync(zone, cancellationToken, ZoneBoundaryRules.ForRafts(boundary, inUseRafts));
    }

    /// <summary>In Use → Discontinued, blocked while the zone still has In Use rafts (MSG-GEN-06).</summary>
    public async Task<Result<FarmingZoneView>> DiscontinueAsync(Guid zoneId, ChangeStatusRequest request, CancellationToken cancellationToken)
    {
        var zone = await zones.GetForEditAsync(zoneId, cancellationToken);
        if (zone is null)
        {
            return AppError.NotFound;
        }

        if (zone.StatusId != StatusIds.Usage.InUse)
        {
            return WrongStatus(zone);
        }

        var raftCodes = await zones.InUseRaftCodesAsync(zone.Id, cancellationToken);
        if (raftCodes.Count > 0)
        {
            return AppError.Conflict(UserMessages.StillInUse(raftCodes.Count, string.Join(", ", raftCodes)), new Dictionary<string, object?> { ["raftCodes"] = raftCodes });
        }

        return await ChangeStatusAsync(zone, StatusIds.Usage.Discontinued, "FarmingZone.Discontinued", request, cancellationToken);
    }

    /// <summary>
    /// FARM-02.3 Resume: Discontinued → In Use with the same code and history, always allowed. Its rafts keep their own
    /// status, and its licenses that are not Replaced are reminded again from the next check (GFR-03).
    /// </summary>
    public async Task<Result<FarmingZoneView>> ReactivateAsync(Guid zoneId, ChangeStatusRequest request, CancellationToken cancellationToken)
    {
        var zone = await zones.GetForEditAsync(zoneId, cancellationToken);
        if (zone is null)
        {
            return AppError.NotFound;
        }

        return zone.StatusId == StatusIds.Usage.Discontinued
            ? await ChangeStatusAsync(zone, StatusIds.Usage.InUse, "FarmingZone.Reactivated", request, cancellationToken)
            : WrongStatus(zone);
    }

    private async Task<Result<FarmingZoneView>> ChangeStatusAsync(
        FarmingZone zone, int newStatusId, string action, ChangeStatusRequest request, CancellationToken cancellationToken)
    {
        var entry = new AuditEntry(action, RecordType) { RecordId = zone.Id, RecordCode = zone.Code, ZoneId = zone.Id };
        return await statusChanges.ChangeAsync(zone, newStatusId, entry, request.Reason, request.RowVersion, cancellationToken) is { } error
            ? error
            : await ViewAsync(zone, cancellationToken);
    }

    private AppError WrongStatus(FarmingZone zone) => statusChanges.WrongStatus($"Farming zone {zone.Code}", zone);

    /// <summary>
    /// Saves the zone. A parallel save of the same code stopped by the unique index answers what the pre-check answers
    /// (MSG-GEN-02 with the existing zone, FARM-02.0.E1), not the generic duplicate error. Null when saved.
    /// </summary>
    private async Task<AppError?> SaveAsync(FarmingZone zone, Guid? exceptZoneId, CancellationToken cancellationToken)
    {
        try
        {
            await unitOfWork.SaveChangesAsync(cancellationToken);
            return null;
        }
        catch (DuplicateRecordException)
        {
            if (await zones.FindByCodeAsync(zone.Code, exceptZoneId, cancellationToken) is { } existing)
            {
                return Duplicate(existing.Id);
            }

            throw;
        }
    }

    /// <summary>The zone with MSG-GEN-05 (FARM-02.0 step 6), the warnings on its pin first, then <paramref name="more"/>.</summary>
    private async Task<FarmingZoneSaved> SavedAsync(FarmingZone zone, CancellationToken cancellationToken, IReadOnlyList<FarmingWarning>? more = null)
    {
        var saved = UserMessages.Saved("Farming zone");
        var warnings = CoordinateRules.ForZone(zone.Latitude, zone.Longitude).Concat(more ?? []).ToList();
        return new FarmingZoneSaved(await ViewAsync(zone, cancellationToken), saved.Text, saved.Code, warnings);
    }

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
        var zoneLicenses = await licenses.ListForZonesAsync(zoneIds, cancellationToken);
        return zoneIds.ToDictionary(id => id, id => ZoneLicenseOverview.BadgeFor(id, zoneLicenses, today));
    }

    private async Task<FarmingZoneView> ViewAsync(FarmingZone zone, CancellationToken cancellationToken)
    {
        var badges = await BadgesAsync([zone.Id], await lookupAccess.VisibleZonesAsync(cancellationToken), cancellationToken);
        var raftCodes = await zones.InUseRaftCodesAsync(zone.Id, cancellationToken);
        return new FarmingZoneView(
            zone.Id, zone.Code, zone.Name, zone.Location, zone.Latitude, zone.Longitude, zone.Area, ZoneBoundaryJson.Read(zone.BoundaryGeoJson),
            zone.StatusId, statusCatalog.DisplayNameOf(zone.StatusId), raftCodes.Count, badges.GetValueOrDefault(zone.Id),
            RowVersionGuard.Encode(zone.RowVersion));
    }

    /// <summary>Code and name are required; location, area and the pin are optional and checked when sent (a pin needs both numbers).</summary>
    /// <param name="ignorePin">The zone has a boundary: the pin sent is discarded, so it is not checked either.</param>
    internal static FieldCheck Validate(SaveFarmingZoneRequest request, out ZoneValues values, bool ignorePin = false)
    {
        var check = new FieldCheck();
        var code = check.Required(request.Code, "Code", SequentialCodes.ZoneCodeMaxLength);
        var name = check.Required(request.Name, "Name", 200);
        var location = check.Optional(request.Location, "Location", 500);
        var (latitude, longitude) = ignorePin ? (null, null) : check.Coordinates(request.Latitude, request.Longitude, required: false);
        check.Check(request.Area is null or (> 0 and < 100_000_000_000_000m), "Area");
        values = new ZoneValues(code, name, location, latitude, longitude, request.Area);
        return check;
    }

    /// <param name="keepPin">The zone has a boundary: its pin is the boundary's centroid and the values sent for it are ignored.</param>
    internal static void Apply(FarmingZone zone, ZoneValues values, bool keepPin)
    {
        zone.Code = values.Code;
        zone.Name = values.Name;
        zone.Location = values.Location;
        if (!keepPin)
        {
            zone.Latitude = values.Latitude;
            zone.Longitude = values.Longitude;
        }

        zone.Area = values.Area;
    }

    private static object Snapshot(FarmingZone z) => new { z.Code, z.Name, z.Location, z.Latitude, z.Longitude, z.Area };

    /// <summary>The boundary as the GeoJSON it is (not as an escaped text), so the audit log shows and compares it as such, with the pin a boundary sets.</summary>
    private static object BoundarySnapshot(FarmingZone z) =>
        new { Boundary = z.BoundaryGeoJson is { } json ? JsonNode.Parse(json) : null, z.Latitude, z.Longitude };

    internal sealed record ZoneValues(string Code, string Name, string? Location, decimal? Latitude, decimal? Longitude, decimal? Area);
}
