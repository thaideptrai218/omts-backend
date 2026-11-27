using OMTS.Application.Common.Concurrency;
using OMTS.Application.Common.Interfaces;
using OMTS.Application.Common.Interfaces.Persistence;
using OMTS.Application.Common.Interfaces.Storage;
using OMTS.Application.Common.Messages;
using OMTS.Application.Common.Results;
using OMTS.Application.Features.Audit;
using OMTS.Application.Features.Cultivation.Cycles;
using OMTS.Application.Features.Cultivation.Progress;
using OMTS.Application.Features.Employees;
using OMTS.Application.Features.Farming.Zones;
using OMTS.Domain.Constants;
using OMTS.Domain.Entities.Farming;

namespace OMTS.Application.Features.Farming.Rafts;

/// <summary>What the raft services share: the raft answers and the GBR-01 refusal of changes to a raft that is read-only.</summary>
public sealed class RaftViews(IRaftMaterialCostQuery materialCosts, IFileStorage storage, IStatusCatalog statusCatalog, StatusChangeService statusChanges)
{
    public async Task<RaftView> ViewAsync(Raft raft, CancellationToken cancellationToken)
    {
        var costs = await materialCosts.GetTotalsAsync([raft.Id], cancellationToken);
        var photos = new List<RaftPhotoView>();
        foreach (var photo in raft.Photos.OrderBy(p => p.SortOrder))
        {
            photos.Add(new RaftPhotoView(photo.Id, photo.SortOrder, await storage.GetReadUrlAsync(photo.FileKey, cancellationToken: cancellationToken)));
        }

        return new RaftView(
            raft.Id,
            new ZoneRef(raft.Zone.Id, raft.Zone.Code, raft.Zone.Name),
            raft.Code, raft.Latitude, raft.Longitude, raft.Location, raft.Size, raft.ConditionNotes,
            photos,
            raft.StatusId, statusCatalog.DisplayNameOf(raft.StatusId),
            RaftLaneChange.InUseCount(raft.Lanes),
            Lanes(raft),
            costs.GetValueOrDefault(raft.Id),
            RowVersionGuard.Encode(raft.RowVersion),
            raft.LengthM,
            raft.WidthM,
            raft.AreaM2,
            RaftGeometry.TotalLineLength(raft),
            raft.Lanes.Count(l => l.StatusId == StatusIds.Lane.InUse && l.IsDamaged),
            raft.Lanes.Count(l => l.StatusId == StatusIds.Lane.InUse && l.IsOutOfService));
    }

    public IReadOnlyList<LaneView> Lanes(Raft raft) => raft.Lanes
        .OrderBy(l => l.LaneNumber)
        .Select(l => new LaneView(
            l.Id, l.LaneNumber, l.StatusId, statusCatalog.DisplayNameOf(l.StatusId),
            RaftGeometry.EffectiveLineLength(l, raft), l.LineLengthM is not null, l.IsDamaged, l.IsOutOfService, l.OutOfServiceOn))
        .ToList();

    /// <summary>Every lane of the raft in number order with its state on a date (BR-FARM-03).</summary>
    /// <param name="progress">The percentages of each lane in the cycle of the day (<see cref="LaneProgressViews"/>); left out, the lanes show none.</param>
    public IReadOnlyList<LaneDayView> LanesOn(Raft raft, IReadOnlyDictionary<Guid, LaneState> states, IReadOnlyDictionary<Guid, LaneProgressInfo>? progress = null) => raft.Lanes
        .OrderBy(l => l.LaneNumber)
        .Select(l =>
        {
            var info = progress?.GetValueOrDefault(l.Id);
            return new LaneDayView(
                l.Id, l.LaneNumber, l.StatusId, statusCatalog.DisplayNameOf(l.StatusId), states[l.Id],
                l.IsDamaged, l.IsOutOfService, RaftGeometry.EffectiveLineLength(l, raft), LaneReasonCodes.Of(l),
                info?.SeededPercent ?? 0m, info?.HarvestedPercent, info?.LastModifiedByName, info?.LastModifiedAt, info?.ProgressHistory);
        })
        .ToList();

    /// <summary>
    /// The raft with MSG-GEN-05 "{what} saved." (FARM-04.0 step 6) and its soft warnings: a pin far from the zone pin or
    /// outside the zone boundary (PM decision 25/11/2026: warn, never block), then its geometry.
    /// </summary>
    public async Task<RaftSaved> SavedAsync(Raft raft, string what, CancellationToken cancellationToken)
    {
        var saved = UserMessages.Saved(what);
        var warnings = CoordinateRules.ForRaft(raft.Latitude, raft.Longitude, raft.Zone.Latitude, raft.Zone.Longitude)
            .Concat(ZoneBoundaryRules.ForRaft(raft.Latitude, raft.Longitude, ZoneBoundaryJson.Read(raft.Zone.BoundaryGeoJson)))
            .Concat(RaftGeometry.Warnings(raft))
            .ToList();
        return new RaftSaved(await ViewAsync(raft, cancellationToken), saved.Text, saved.Code, warnings);
    }

    /// <summary>
    /// GBR-01, FARM-04.2.E2: MSG-GEN-09 when the raft or its zone is Discontinued, null when it may be changed. A Discontinued
    /// raft is read-only until it is resumed; a raft In Use in a Discontinued zone only arises from an older database.
    /// </summary>
    public AppError? EditRefusal(Raft raft) =>
        raft.StatusId != StatusIds.Usage.InUse ? statusChanges.WrongStatus($"Raft {raft.Code}", raft)
        : raft.Zone.StatusId != StatusIds.Usage.InUse ? statusChanges.WrongStatus($"Farming zone {raft.Zone.Code}", raft.Zone)
        : null;

    /// <summary>
    /// Care logs and material usage may be recorded on a raft In Use or Discontinued, so the repair of a Discontinued raft is kept before it is turned back on; the zone must still be In Use.
    /// </summary>
    public static bool AcceptsCareRecords(Raft raft) =>
        raft.StatusId is (StatusIds.Usage.InUse or StatusIds.Usage.Discontinued) && raft.Zone.StatusId == StatusIds.Usage.InUse;

    /// <summary>MSG-GEN-09 when the raft cannot take care logs and material usage (<see cref="AcceptsCareRecords"/>), null when it can. The raft comes with its zone.</summary>
    public AppError? CareRecordRefusal(Raft raft) =>
        AcceptsCareRecords(raft) ? null
        : raft.Zone.StatusId != StatusIds.Usage.InUse ? statusChanges.WrongStatus($"Farming zone {raft.Zone.Code}", raft.Zone)
        : statusChanges.WrongStatus($"Raft {raft.Code}", raft);
}
