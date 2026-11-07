using OMTS.Application.Features.Cultivation.Progress;
using OMTS.Domain.Entities.Cultivation;

namespace OMTS.Application.Common.Interfaces.Persistence;

public interface ISeedingRecordRepository
{
    /// <summary>The session with its lanes, its cycle, raft and zone, and its seed lot with receipt, supplier and item, tracked for editing.</summary>
    Task<SeedingRecord?> GetForEditAsync(Guid recordId, CancellationToken cancellationToken);

    /// <summary>The same, read only.</summary>
    Task<SeedingRecord?> GetAsync(Guid recordId, CancellationToken cancellationToken);

    /// <summary>The sessions of a cycle with their lanes and seed lots, read only, the latest date first.</summary>
    Task<IReadOnlyList<SeedingRecord>> ListByCycleAsync(Guid cycleId, CancellationToken cancellationToken);

    /// <summary>Each lane of each session of the cycle with its date: what the lane states are worked out from (BR-FARM-03).</summary>
    Task<IReadOnlyList<LaneSeeding>> LaneSeedingsAsync(Guid cycleId, CancellationToken cancellationToken);

    /// <summary>Adds the session with the lanes it holds.</summary>
    void Add(SeedingRecord record);

    void AddLane(SeedingRecordLane lane);

    void RemoveLane(SeedingRecordLane lane);

    /// <summary>Removes the session with the lanes it holds; the foreign key does not cascade.</summary>
    void Remove(SeedingRecord record);
}
