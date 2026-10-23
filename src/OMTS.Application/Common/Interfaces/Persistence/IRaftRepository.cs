using OMTS.Domain.Entities.Cultivation;
using OMTS.Domain.Entities.Farming;
using OMTS.Domain.Enums;

namespace OMTS.Application.Common.Interfaces.Persistence;

/// <summary>A recorded lane segment with the names and codes the raft condition view shows.</summary>
public sealed record LaneSegmentDetail(
    Guid Id,
    Guid LaneId,
    LaneSegmentType Type,
    decimal From,
    decimal To,
    DateOnly Date,
    string? RecordedBy,
    string? ConfirmedBy,
    Guid? SeedingEventId,
    string? SeedLotCode,
    Guid? HarvestEventId,
    Guid? LotId,
    string? WriteOffReason);

public interface IRaftRepository
{
    /// <summary>Raft with its zone, lanes and layout versions, tracked for editing.</summary>
    Task<Raft?> GetForEditAsync(Guid raftId, CancellationToken cancellationToken);

    /// <summary>Another raft of the zone with this code (ignoring case).</summary>
    Task<Raft?> FindByCodeAsync(Guid zoneId, string code, Guid? exceptRaftId, CancellationToken cancellationToken);

    /// <summary>Rafts of a zone, optional status, ordered by code.</summary>
    Task<IReadOnlyList<Raft>> ListByZoneAsync(Guid zoneId, int? statusId, CancellationToken cancellationToken);

    Task<CultivationCycle?> GetOpenCycleAsync(Guid raftId, CancellationToken cancellationToken);

    /// <summary>The given cycle of the raft, or the cycle that was open on the date.</summary>
    Task<CultivationCycle?> FindCycleAsync(Guid raftId, Guid? cycleId, DateOnly date, CancellationToken cancellationToken);

    /// <summary>
    /// Lane numbers among <paramref name="laneIds"/> that records point to: a segment in any cycle or a
    /// cultivation log entry (both keep their lane, so such a lane cannot be removed).
    /// </summary>
    Task<IReadOnlyList<int>> LanesInUseAsync(IReadOnlyCollection<Guid> laneIds, CancellationToken cancellationToken);

    /// <summary>Segments of a cycle dated up to <paramref name="date"/>.</summary>
    Task<IReadOnlyList<LaneSegmentDetail>> SegmentsAsync(Guid cycleId, DateOnly date, CancellationToken cancellationToken);

    void Add(Raft raft);

    void AddLane(Lane lane);

    /// <summary>Lanes without segments only (checked by the caller).</summary>
    void RemoveLane(Lane lane);

    void AddLayoutVersion(RaftLayoutVersion version);
}
