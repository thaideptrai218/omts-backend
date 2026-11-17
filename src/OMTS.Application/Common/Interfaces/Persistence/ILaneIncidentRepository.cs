using OMTS.Domain.Entities.Farming;

namespace OMTS.Application.Common.Interfaces.Persistence;

/// <summary>The history of the lanes of rafts (decision of 12/11/2026).</summary>
public interface ILaneIncidentRepository
{
    /// <summary>The incident with its photos and materials, tracked for editing.</summary>
    Task<LaneIncident?> GetForEditAsync(Guid incidentId, CancellationToken cancellationToken);

    /// <summary>The incidents of the lanes of a raft, read only, newest first; one lane when <paramref name="laneId"/> is given.</summary>
    Task<IReadOnlyList<LaneIncident>> ListOfRaftAsync(Guid raftId, Guid? laneId, CancellationToken cancellationToken);

    /// <summary>The out-of-service incidents of the cycle that had seed (the loss events), read only, in lane order.</summary>
    Task<IReadOnlyList<LaneIncident>> LossEventsOfCycleAsync(Guid cycleId, CancellationToken cancellationToken);

    /// <summary>The latest damage report of the lane not yet resolved by a repair, tracked.</summary>
    Task<LaneIncident?> OpenDamageOfLaneAsync(Guid laneId, CancellationToken cancellationToken);

    void Add(LaneIncident incident);

    void AddPhoto(LaneIncidentPhoto photo);

    void AddMaterial(LaneIncidentMaterial material);
}
