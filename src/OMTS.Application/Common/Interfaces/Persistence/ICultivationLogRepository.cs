using OMTS.Domain.Entities.Cultivation;

namespace OMTS.Application.Common.Interfaces.Persistence;

public interface ICultivationLogRepository
{
    /// <summary>The log with its photos, cycle, raft and zone, tracked for editing.</summary>
    Task<CultivationLog?> GetForEditAsync(Guid logId, CancellationToken cancellationToken);

    /// <summary>The log with its photos, cycle, raft and zone, read only.</summary>
    Task<CultivationLog?> GetAsync(Guid logId, CancellationToken cancellationToken);

    /// <summary>The logs of a cycle with their photos, the latest visit first.</summary>
    Task<IReadOnlyList<CultivationLog>> ListByCycleAsync(Guid cycleId, CancellationToken cancellationToken);

    /// <summary>Adds the log with the photos it holds.</summary>
    void Add(CultivationLog log);

    void AddPhoto(CultivationLogPhoto photo);

    void RemovePhoto(CultivationLogPhoto photo);

    /// <summary>Removes the log with the photos it holds; the foreign key does not cascade.</summary>
    void Remove(CultivationLog log);
}
