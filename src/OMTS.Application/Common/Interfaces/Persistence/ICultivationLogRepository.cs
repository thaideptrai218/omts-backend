using OMTS.Domain.Entities.Cultivation;

namespace OMTS.Application.Common.Interfaces.Persistence;

/// <summary>Filters of the cross-raft log list; null means "any". A caller who reads only some zones passes them in <see cref="ZoneIds"/>. The period is on the visit date, inclusive.</summary>
public sealed record CultivationLogFilter(
    Guid? ZoneId, Guid? RaftId, IReadOnlySet<Guid>? ZoneIds, string? LogType, DateOnly? From, DateOnly? To, string? Search);

public interface ICultivationLogRepository
{
    /// <summary>The log with its photos, cycle, raft and zone, tracked for editing.</summary>
    Task<CultivationLog?> GetForEditAsync(Guid logId, CancellationToken cancellationToken);

    /// <summary>The log with its photos, cycle, raft and zone, read only.</summary>
    Task<CultivationLog?> GetAsync(Guid logId, CancellationToken cancellationToken);

    /// <summary>The logs of a cycle with their photos, the latest visit first; only those of <paramref name="logType"/> when it is not null.</summary>
    Task<IReadOnlyList<CultivationLog>> ListByCycleAsync(Guid cycleId, string? logType, CancellationToken cancellationToken);

    /// <summary>One page of the logs that match, the latest visit first, with photos, cycle, raft and zone, and how many match: the count, the page, and one read for the photos of the page.</summary>
    Task<(IReadOnlyList<CultivationLog> Rows, int Total)> SearchAsync(CultivationLogFilter filter, int page, int pageSize, CancellationToken cancellationToken);

    /// <summary>Adds the log with the photos it holds.</summary>
    void Add(CultivationLog log);

    void AddPhoto(CultivationLogPhoto photo);

    void RemovePhoto(CultivationLogPhoto photo);

    /// <summary>Removes the log with the photos it holds; the foreign key does not cascade.</summary>
    void Remove(CultivationLog log);
}
