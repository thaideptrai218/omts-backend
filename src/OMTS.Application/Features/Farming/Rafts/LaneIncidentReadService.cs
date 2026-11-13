using OMTS.Application.Common.Interfaces.Persistence;
using OMTS.Application.Common.Results;
using OMTS.Application.Features.Authorization;
using OMTS.Application.Features.Authorization.Models;

namespace OMTS.Application.Features.Farming.Rafts;

/// <summary>The history of the lanes of a raft (decision of 12/11/2026), for the Owner and a Zone Manager of the raft's zone: who did what and when, why, with which materials. Read only.</summary>
public sealed class LaneIncidentReadService(IRaftRepository rafts, ILaneIncidentRepository incidents, LaneIncidentViews views, ZoneAccessGuard zoneAccess)
{
    /// <summary>The entries of every lane of the raft, or of one lane, newest first. 404 for no such raft or lane.</summary>
    public async Task<Result<LaneHistory>> HistoryAsync(Guid raftId, int? laneNumber, CancellationToken cancellationToken)
    {
        var raft = await rafts.GetForEditAsync(raftId, cancellationToken);
        if (raft is null)
        {
            return AppError.NotFound;
        }

        if (!await zoneAccess.AllowsAsync(PermissionFunctions.CultivationCycles, PermissionAction.View, raft.ZoneId, cancellationToken))
        {
            return AppError.Forbidden;
        }

        var lane = laneNumber is { } number ? raft.Lanes.FirstOrDefault(l => l.LaneNumber == number) : null;
        if (laneNumber is not null && lane is null)
        {
            return AppError.NotFound;
        }

        var found = await incidents.ListOfRaftAsync(raft.Id, lane?.Id, cancellationToken);
        return new LaneHistory(raft.Id, raft.Code, laneNumber, await views.ViewAsync(found, cancellationToken));
    }
}
