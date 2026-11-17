using OMTS.Domain.Entities.Cultivation;

namespace OMTS.Application.Common.Interfaces.Persistence;

public interface IMaterialUsageRepository
{
    /// <summary>The record with its item, raft and zone, tracked for editing.</summary>
    Task<MaterialUsage?> GetForEditAsync(Guid usageId, CancellationToken cancellationToken);

    /// <summary>The record with its item, raft and zone, read only.</summary>
    Task<MaterialUsage?> GetAsync(Guid usageId, CancellationToken cancellationToken);

    /// <summary>The records of a raft with their items, read only: the latest repair first, then by reason and item code.</summary>
    Task<IReadOnlyList<MaterialUsage>> ListByRaftAsync(Guid raftId, CancellationToken cancellationToken);

    void Add(MaterialUsage usage);

    /// <summary>Removes the record; the audit log keeps it with the reason.</summary>
    void Remove(MaterialUsage usage);
}
