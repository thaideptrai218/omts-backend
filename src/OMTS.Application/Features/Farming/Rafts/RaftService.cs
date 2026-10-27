using OMTS.Application.Common;
using OMTS.Application.Common.Concurrency;
using OMTS.Application.Common.Interfaces;
using OMTS.Application.Common.Interfaces.Persistence;
using OMTS.Application.Common.Interfaces.Storage;
using OMTS.Application.Common.Messages;
using OMTS.Application.Common.Results;
using OMTS.Application.Features.Audit;
using OMTS.Application.Features.Audit.Models;
using OMTS.Application.Features.Employees;
using OMTS.Application.Features.Farming.Zones;
using OMTS.Application.Features.Files;
using OMTS.Domain.Constants;
using OMTS.Domain.Entities.Farming;

namespace OMTS.Application.Features.Farming.Rafts;

/// <summary>
/// FARM-04 – rafts and their versioned lane layout. Writes are Cooperative Owner only (API filter); reads
/// are open to staff of the raft's zone. A layout change is blocked while a cultivation cycle is open.
/// </summary>
public sealed class RaftService(
    IRaftRepository rafts,
    IFarmingZoneRepository zones,
    IRaftMaterialCostQuery materialCosts,
    FarmingLookupAccess lookupAccess,
    IFileStorage storage,
    UploadedFileSaver fileSaver,
    IStatusCatalog statusCatalog,
    RowVersionGuard rowVersions,
    StatusChangeService statusChanges,
    AuditLogService audit,
    IUnitOfWork unitOfWork,
    TimeProvider timeProvider)
{
    private const string RecordType = nameof(Raft);

    public async Task<Result<IReadOnlyList<RaftListItem>>> ListAsync(Guid zoneId, int? statusId, CancellationToken cancellationToken)
    {
        if ((await zones.GetByIdsAsync([zoneId], cancellationToken)).Count == 0)
        {
            return AppError.NotFound;
        }

        if (!await lookupAccess.CanSeeZoneAsync(zoneId, cancellationToken))
        {
            return AppError.Forbidden;
        }

        var found = await rafts.ListByZoneAsync(zoneId, statusId, cancellationToken);
        return found.Select(r => new RaftListItem(
            r.Id, r.ZoneId, r.Code, r.Latitude, r.Longitude, r.Size, r.Lanes.Count, r.StatusId, statusCatalog.DisplayNameOf(r.StatusId))).ToList();
    }

    public async Task<Result<RaftView>> GetAsync(Guid raftId, CancellationToken cancellationToken)
    {
        var raft = await rafts.GetForEditAsync(raftId, cancellationToken);
        if (raft is null)
        {
            return AppError.NotFound;
        }

        return await lookupAccess.CanSeeZoneAsync(raft.ZoneId, cancellationToken) ? await ViewAsync(raft, cancellationToken) : AppError.Forbidden;
    }

    public async Task<Result<RaftView>> CreateAsync(Guid zoneId, SaveRaftRequest request, CancellationToken cancellationToken)
    {
        var zone = await zones.GetForEditAsync(zoneId, cancellationToken);
        if (zone is null)
        {
            return AppError.NotFound;
        }

        var check = new FieldCheck();
        check.Check(zone.StatusId == StatusIds.Usage.InUse, "Zone");
        // The zone must still be In Use when this save commits: a parallel discontinue then fails with MSG06.
        unitOfWork.MarkChanged(zone);
        var values = Read(request, check, out var lanes, out var drawing);
        if (!check.IsValid)
        {
            return AppError.InvalidFields(check.Invalid);
        }

        if (await rafts.FindByCodeAsync(zone.Id, values.Code, null, cancellationToken) is { } existing)
        {
            return Duplicate(existing.Id);
        }

        var raft = new Raft { ZoneId = zone.Id, Zone = zone };
        Apply(raft, values);
        rafts.Add(raft);
        foreach (var lane in lanes!)
        {
            raft.Lanes.Add(new Lane { RaftId = raft.Id, LaneNumber = lane.LaneNumber, Length = lane.Length, MeasurementMarkers = RaftLayout.Markers(lane) });
        }

        raft.LayoutVersions.Add(new RaftLayoutVersion
        {
            RaftId = raft.Id, VersionNumber = 1, LaneCount = lanes.Count, LayoutMap = RaftLayout.Snapshot(lanes, drawing), ValidFrom = timeProvider.Today(),
        });
        audit.Record(new AuditEntry("Raft.Created", RecordType)
        {
            RecordId = raft.Id, RecordCode = raft.Code, ZoneId = raft.ZoneId, After = new { Raft = Snapshot(raft), Lanes = lanes },
        });
        await unitOfWork.SaveChangesAsync(cancellationToken);
        return await ViewAsync(raft, cancellationToken);
    }

    /// <summary>Edits the raft; a different lane layout becomes a new layout version.</summary>
    public async Task<Result<RaftView>> UpdateAsync(Guid raftId, SaveRaftRequest request, CancellationToken cancellationToken)
    {
        var raft = await rafts.GetForEditAsync(raftId, cancellationToken);
        if (raft is null)
        {
            return AppError.NotFound;
        }

        var check = new FieldCheck();
        var values = Read(request, check, out var lanes, out var drawing);
        if (!check.IsValid)
        {
            return AppError.InvalidFields(check.Invalid);
        }

        if (await rafts.FindByCodeAsync(raft.ZoneId, values.Code, raft.Id, cancellationToken) is { } existing)
        {
            return Duplicate(existing.Id);
        }

        if (await rowVersions.CheckAsync(raft, request.RowVersion, cancellationToken) is { } conflict)
        {
            return conflict;
        }

        var layoutChange = RaftLayout.DiffersFrom(lanes!, raft.Lanes)
            ? await ChangeLayoutAsync(raft, lanes!, drawing, cancellationToken)
            : KeepLayout(raft, lanes!, drawing);
        if (layoutChange.Error is { } blocked)
        {
            return blocked;
        }

        // Lanes and layout versions are rows of their own: the raft must still get a new row version (MSG06 for stale tabs).
        if (layoutChange.Changed)
        {
            unitOfWork.MarkChanged(raft);
        }

        var before = Snapshot(raft);
        Apply(raft, values);
        var (changedBefore, changedAfter) = AuditDiff.Changed(before, Snapshot(raft));
        if (changedAfter is not null)
        {
            audit.Record(new AuditEntry("Raft.Updated", RecordType)
            {
                RecordId = raft.Id, RecordCode = raft.Code, ZoneId = raft.ZoneId, Before = changedBefore, After = changedAfter,
            });
        }

        await unitOfWork.SaveChangesAsync(cancellationToken);
        return await ViewAsync(raft, cancellationToken);
    }

    /// <summary>
    /// JPEG/PNG photo, at most 10 MB, stored unchanged; a storage failure changes nothing (MSG05). The replaced
    /// photo file is removed once the new one is saved.
    /// </summary>
    public async Task<Result<RaftView>> UploadPhotoAsync(Guid raftId, IncomingFile file, CancellationToken cancellationToken)
    {
        var raft = await rafts.GetForEditAsync(raftId, cancellationToken);
        if (raft is null)
        {
            return AppError.NotFound;
        }

        var validated = FileValidator.Validate([file], FileRules.Photos with { MaxFiles = 1 });
        if (!validated.IsValid)
        {
            return AppError.Invalid("Photo");
        }

        var uploaded = await storage.UploadAsync(validated.Files[0], FileCategories.RaftPhotos, cancellationToken);
        if (!uploaded.Succeeded)
        {
            return new AppError(ErrorKind.BadGateway, UserMessages.FileOperationFailed);
        }

        var replaced = raft.PhotoUrl;
        audit.Record(new AuditEntry("Raft.Updated", RecordType)
        {
            RecordId = raft.Id, RecordCode = raft.Code, ZoneId = raft.ZoneId, Before = new { raft.PhotoUrl }, After = new { PhotoUrl = uploaded.Key },
        });
        raft.PhotoUrl = uploaded.Key;
        await fileSaver.SaveAsync(uploaded.Key, replaced, cancellationToken);
        return await ViewAsync(raft, cancellationToken);
    }

    /// <summary>In Use → Discontinued, only without an open cultivation cycle.</summary>
    public async Task<Result<RaftView>> DiscontinueAsync(Guid raftId, ChangeStatusRequest request, CancellationToken cancellationToken)
    {
        var raft = await rafts.GetForEditAsync(raftId, cancellationToken);
        if (raft is null)
        {
            return AppError.NotFound;
        }

        if (raft.StatusId != StatusIds.Usage.InUse)
        {
            return WrongStatus(raft, StatusIds.Usage.Discontinued, "the raft is already discontinued");
        }

        if (await rafts.GetOpenCycleAsync(raft.Id, cancellationToken) is not null)
        {
            return WrongStatus(raft, StatusIds.Usage.Discontinued, "the raft has an open cultivation cycle");
        }

        return await ChangeStatusAsync(raft, StatusIds.Usage.Discontinued, "Raft.Discontinued", request, cancellationToken);
    }

    /// <summary>Discontinued → In Use, only when the zone is In Use.</summary>
    public async Task<Result<RaftView>> ReactivateAsync(Guid raftId, ChangeStatusRequest request, CancellationToken cancellationToken)
    {
        var raft = await rafts.GetForEditAsync(raftId, cancellationToken);
        if (raft is null)
        {
            return AppError.NotFound;
        }

        if (raft.StatusId != StatusIds.Usage.Discontinued)
        {
            return WrongStatus(raft, StatusIds.Usage.InUse, "the raft is already in use");
        }

        // As on create: the zone row is part of this save, so a parallel zone discontinue fails with MSG06.
        unitOfWork.MarkChanged(raft.Zone);
        return raft.Zone.StatusId == StatusIds.Usage.InUse
            ? await ChangeStatusAsync(raft, StatusIds.Usage.InUse, "Raft.Reactivated", request, cancellationToken)
            : WrongStatus(raft, StatusIds.Usage.InUse, "the zone is discontinued");
    }

    private async Task<LayoutOutcome> ChangeLayoutAsync(Raft raft, IReadOnlyList<LayoutLane> lanes, string? drawing, CancellationToken cancellationToken)
    {
        if (await rafts.GetOpenCycleAsync(raft.Id, cancellationToken) is { } cycle)
        {
            var name = $"cultivation cycle opened {BusinessClock.ToBusinessDate(cycle.OpenedAt):dd/MM/yyyy}";
            return new LayoutOutcome(AppError.Conflict(UserMessages.StillInUse(1, name), new Dictionary<string, object?> { ["cycleId"] = cycle.Id }), false);
        }

        var removed = raft.Lanes.Where(l => l.LaneNumber > lanes.Count).ToList();
        var inUse = removed.Count == 0 ? [] : await rafts.LanesInUseAsync(removed.Select(l => l.Id).ToList(), cancellationToken);
        if (inUse.Count > 0)
        {
            return new LayoutOutcome(AppError.Conflict(
                UserMessages.StillInUse(inUse.Count, string.Join(", ", inUse.Select(n => $"lane {n}"))),
                new Dictionary<string, object?> { ["laneNumbers"] = inUse }), false);
        }

        foreach (var lane in removed)
        {
            raft.Lanes.Remove(lane);
            rafts.RemoveLane(lane);
        }

        foreach (var lane in lanes)
        {
            if (raft.Lanes.FirstOrDefault(l => l.LaneNumber == lane.LaneNumber) is { } existing)
            {
                (existing.Length, existing.MeasurementMarkers) = (lane.Length, RaftLayout.Markers(lane));
            }
            else
            {
                var added = new Lane { RaftId = raft.Id, LaneNumber = lane.LaneNumber, Length = lane.Length, MeasurementMarkers = RaftLayout.Markers(lane) };
                raft.Lanes.Add(added);
                rafts.AddLane(added);
            }
        }

        var today = timeProvider.Today();
        var current = raft.LayoutVersions.MaxBy(v => v.VersionNumber);
        if (current is not null)
        {
            current.ValidTo = today;
        }

        var version = new RaftLayoutVersion
        {
            RaftId = raft.Id, VersionNumber = (current?.VersionNumber ?? 0) + 1, LaneCount = lanes.Count,
            LayoutMap = RaftLayout.Snapshot(lanes, drawing), ValidFrom = today,
        };
        raft.LayoutVersions.Add(version);
        rafts.AddLayoutVersion(version);
        audit.Record(new AuditEntry("Raft.LayoutChanged", RecordType)
        {
            RecordId = raft.Id,
            RecordCode = raft.Code,
            ZoneId = raft.ZoneId,
            Before = new { Version = current?.VersionNumber, LaneCount = current?.LaneCount },
            After = new { Version = version.VersionNumber, version.LaneCount, Lanes = lanes },
        });
        return new LayoutOutcome(null, Changed: true);
    }

    /// <summary>Same lanes: only a changed drawing is written into the current version (audited, no new version).</summary>
    private LayoutOutcome KeepLayout(Raft raft, IReadOnlyList<LayoutLane> lanes, string? drawing)
    {
        var current = raft.LayoutVersions.MaxBy(v => v.VersionNumber);
        if (current is null || RaftLayout.SameDrawing(current, drawing))
        {
            return new LayoutOutcome(null, Changed: false);
        }

        current.LayoutMap = RaftLayout.Snapshot(lanes, drawing);
        audit.Record(new AuditEntry("Raft.LayoutDrawingChanged", RecordType)
        {
            RecordId = raft.Id, RecordCode = raft.Code, ZoneId = raft.ZoneId, After = new { Version = current.VersionNumber, HasDrawing = drawing is not null },
        });
        return new LayoutOutcome(null, Changed: true);
    }

    private async Task<Result<RaftView>> ChangeStatusAsync(Raft raft, int newStatusId, string action, ChangeStatusRequest request, CancellationToken cancellationToken)
    {
        var entry = new AuditEntry(action, RecordType) { RecordId = raft.Id, RecordCode = raft.Code, ZoneId = raft.ZoneId };
        return await statusChanges.ChangeAsync(raft, newStatusId, entry, request.Reason, request.RowVersion, cancellationToken) is { } error
            ? error
            : await ViewAsync(raft, cancellationToken);
    }

    private AppError WrongStatus(Raft raft, int newStatusId, string reason) => statusChanges.WrongStatus("raft", raft, newStatusId, reason);

    private static AppError Duplicate(Guid existingId) => new(
        ErrorKind.Conflict,
        UserMessages.AlreadyExists("Raft"),
        new Dictionary<string, object?> { ["existingId"] = existingId, ["existingRaftId"] = existingId });

    private static RaftValues Read(SaveRaftRequest request, FieldCheck check, out IReadOnlyList<LayoutLane>? lanes, out string? drawing)
    {
        var code = check.Required(request.Code, "Code", 50);
        check.Coordinates(request.Latitude, request.Longitude, required: true);
        var location = check.Optional(request.Location, "Location", 500);
        var size = check.Required(request.Size, "Size", 100);
        var notes = check.Optional(request.ConditionNotes, "Condition notes", 2000);
        lanes = RaftLayout.Read(request.Lanes, check);
        drawing = RaftLayout.ReadDrawing(request.LayoutDrawing, check);
        return new RaftValues(code, request.Latitude, request.Longitude, location, size, notes);
    }

    private static void Apply(Raft raft, RaftValues values)
    {
        raft.Code = values.Code;
        raft.Latitude = values.Latitude;
        raft.Longitude = values.Longitude;
        raft.Location = values.Location;
        raft.Size = values.Size;
        raft.ConditionNotes = values.ConditionNotes;
    }

    private static object Snapshot(Raft r) => new { r.Code, r.Latitude, r.Longitude, r.Location, r.Size, r.ConditionNotes };

    private async Task<RaftView> ViewAsync(Raft raft, CancellationToken cancellationToken)
    {
        var current = raft.LayoutVersions.MaxBy(v => v.VersionNumber);
        var costs = await materialCosts.GetTotalsAsync([raft.Id], cancellationToken);
        var photoUrl = raft.PhotoUrl is null ? null : await storage.GetReadUrlAsync(raft.PhotoUrl, cancellationToken: cancellationToken);
        return new RaftView(
            raft.Id,
            new ZoneRef(raft.Zone.Id, raft.Zone.Code, raft.Zone.Name),
            raft.Code, raft.Latitude, raft.Longitude, raft.Location, raft.Size, raft.ConditionNotes, photoUrl,
            raft.StatusId, statusCatalog.DisplayNameOf(raft.StatusId),
            current?.VersionNumber ?? 0,
            raft.Lanes.OrderBy(l => l.LaneNumber).Select(l =>
            {
                var (markerA, markerB) = RaftLayout.ReadMarkers(l.MeasurementMarkers);
                return new LaneView(l.Id, l.LaneNumber, l.Length, markerA, markerB);
            }).ToList(),
            RaftLayout.DrawingOf(current),
            raft.LayoutVersions.OrderBy(v => v.VersionNumber).Select(v => new LayoutVersionView(v.VersionNumber, v.LaneCount, v.ValidFrom, v.ValidTo)).ToList(),
            costs?.GetValueOrDefault(raft.Id),
            RowVersionGuard.Encode(raft.RowVersion));
    }

    private sealed record RaftValues(string Code, decimal? Latitude, decimal? Longitude, string? Location, string Size, string? ConditionNotes);

    private sealed record LayoutOutcome(AppError? Error, bool Changed);
}
