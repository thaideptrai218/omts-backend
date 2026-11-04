using OMTS.Application.Common;
using OMTS.Application.Common.Interfaces;
using OMTS.Application.Common.Interfaces.Persistence;
using OMTS.Application.Common.Results;

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
    IStatusCatalog statusCatalog,
    TimeProvider timeProvider)
{
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

        var found = await rafts.ListByZoneAsync(zoneId, statusId, cancellationToken);
        return found.Select(r => new RaftListItem(
            r.Id, r.ZoneId, r.Code, r.Latitude, r.Longitude, r.Size, RaftLaneChange.InUseCount(r.Lanes), r.StatusId, statusCatalog.DisplayNameOf(r.StatusId))).ToList();
    }

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

    /// <summary>The lanes of a raft today with their status; CYCLE adds the state worked out from its records (BR-FARM-03).</summary>
    public async Task<Result<RaftLanesToday>> LanesTodayAsync(Guid raftId, CancellationToken cancellationToken)
    {
        var raft = await rafts.GetForEditAsync(raftId, cancellationToken);
        if (raft is null)
        {
            return AppError.NotFound;
        }

        return (await lookupAccess.VisibleRaftZonesAsync(cancellationToken)).Allows(raft.ZoneId)
            ? new RaftLanesToday(raft.Id, raft.Code, timeProvider.Today(), views.Lanes(raft))
            : AppError.Forbidden;
    }
}
