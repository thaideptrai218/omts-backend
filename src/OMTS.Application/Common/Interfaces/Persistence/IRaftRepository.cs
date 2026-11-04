using OMTS.Domain.Entities.Cultivation;
using OMTS.Domain.Entities.Farming;

namespace OMTS.Application.Common.Interfaces.Persistence;

public interface IRaftRepository
{
    /// <summary>Raft with its zone, lanes and photos, tracked for editing.</summary>
    Task<Raft?> GetForEditAsync(Guid raftId, CancellationToken cancellationToken);

    /// <summary>Another raft of the zone with this code (ignoring case).</summary>
    Task<Raft?> FindByCodeAsync(Guid zoneId, string code, Guid? exceptRaftId, CancellationToken cancellationToken);

    /// <summary>Codes of every raft of the zone, Discontinued ones included (their codes stay reserved, GBR-03).</summary>
    Task<IReadOnlyList<string>> CodesOfZoneAsync(Guid zoneId, CancellationToken cancellationToken);

    /// <summary>Rafts of a zone with their lanes, optional status, ordered by code.</summary>
    Task<IReadOnlyList<Raft>> ListByZoneAsync(Guid zoneId, int? statusId, CancellationToken cancellationToken);

    Task<CultivationCycle?> GetOpenCycleAsync(Guid raftId, CancellationToken cancellationToken);

    void Add(Raft raft);

    /// <summary>A new lane of a raft being edited. Lanes are never deleted (BR-FARM-04).</summary>
    void AddLane(Lane lane);

    void AddPhoto(RaftPhoto photo);

    void RemovePhoto(RaftPhoto photo);
}
