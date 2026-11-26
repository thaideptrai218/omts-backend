using OMTS.Application.Common;
using OMTS.Application.Common.Interfaces;
using OMTS.Application.Common.Interfaces.Persistence;
using OMTS.Application.Common.Results;
using OMTS.Application.Features.AccessManagement.Dtos;

namespace OMTS.Application.Features.Farming.Rafts;

/// <summary>
/// FARM-04 reads, open to the Cooperative Owner and to a Zone Manager in its zones (BR-FARM-08; it needs them for the CYCLE,
/// HARVEST and WATER screens), not to an Accountant (<see cref="FarmingLookupAccess.VisibleRaftZonesAsync"/>). The code
/// proposal is for the Cooperative Owner (API filter).
/// </summary>
public sealed class RaftReadService(
    IRaftRepository rafts,
    IFarmingZoneRepository zones,
    RaftViews views,
    FarmingLookupAccess lookupAccess,
    IStatusCatalog statusCatalog)
{
    private const int MaxPageSize = 100;

    public async Task<Result<IReadOnlyList<RaftListItem>>> ListAsync(Guid zoneId, int? statusId, CancellationToken cancellationToken)
    {
        if ((await zones.GetByIdsAsync([zoneId], cancellationToken)).Count == 0)
        {
            return AppError.NotFound;
        }

        if (!(await lookupAccess.VisibleRaftZonesAsync(cancellationToken)).Allows(zoneId))
        {
            return AppError.Forbidden;
        }

        var found = await rafts.ListRowsByZoneAsync(zoneId, statusId, cancellationToken);
        return found.Select(Item).ToList();
    }

    /// <summary>
    /// A page of rafts by code, newest code first, each with its Open cycle: of one zone, or of every zone the caller may read. The same
    /// access as <see cref="ListAsync"/>: a zone the caller may not read is 403, a missing one 404.
    /// </summary>
    public async Task<Result<Paged<RaftListItem>>> SearchAsync(
        Guid? zoneId, int? statusId, string? search, int page, int pageSize, CancellationToken cancellationToken)
    {
        var visible = await lookupAccess.VisibleRaftZonesAsync(cancellationToken);
        if (zoneId is { } zone)
        {
            if ((await zones.GetByIdsAsync([zone], cancellationToken)).Count == 0)
            {
                return AppError.NotFound;
            }

            if (!visible.Allows(zone))
            {
                return AppError.Forbidden;
            }
        }
        else if (visible.IsEmpty)
        {
            return AppError.Forbidden;
        }

        // The upper bound keeps (page - 1) * pageSize inside int.
        (page, pageSize) = (Math.Clamp(page, 1, int.MaxValue / MaxPageSize), Math.Clamp(pageSize, 1, MaxPageSize));
        var (items, total) = await rafts.SearchRowsAsync(visible, zoneId, statusId, search, page, pageSize, cancellationToken);
        return new Paged<RaftListItem>(items.Select(Item).ToList(), page, pageSize, total);
    }

    private RaftListItem Item(RaftListRow row) => new(
        row.Raft.Id, row.Raft.ZoneId, row.Raft.Code, row.Raft.Latitude, row.Raft.Longitude, row.Raft.Size, row.LaneCount, row.Raft.StatusId,
        statusCatalog.DisplayNameOf(row.Raft.StatusId), row.Raft.LengthM, row.Raft.WidthM, row.Raft.AreaM2, row.DamagedLaneCount, row.OutOfServiceLaneCount,
        row.CurrentCycle);

    public async Task<Result<RaftView>> GetAsync(Guid raftId, CancellationToken cancellationToken)
    {
        var raft = await rafts.GetForEditAsync(raftId, cancellationToken);
        if (raft is null)
        {
            return AppError.NotFound;
        }

        return (await lookupAccess.VisibleRaftZonesAsync(cancellationToken)).Allows(raft.ZoneId) ? await views.ViewAsync(raft, cancellationToken) : AppError.Forbidden;
    }

    /// <summary>The proposed code of a new raft of the zone ({zone code}-B01, -B02…; GBR-03); the Owner may replace it.</summary>
    public async Task<Result<RaftCodeProposal>> ProposeCodeAsync(Guid zoneId, CancellationToken cancellationToken)
    {
        var found = await zones.GetByIdsAsync([zoneId], cancellationToken);
        return found.Count == 0
            ? AppError.NotFound
            : new RaftCodeProposal(SequentialCodes.NextRaft(found[0].Code, await rafts.CodesOfZoneAsync(zoneId, cancellationToken)));
    }
}
