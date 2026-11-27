using OMTS.Domain.Entities.Cultivation;

namespace OMTS.Application.Common.Interfaces.Persistence;

/// <summary>The column the cross-raft log list is ordered by.</summary>
public enum CultivationLogSort
{
    VisitDate,
    Zone,
    Raft,
    LogType,
    RecordedBy,
    RecordedAt,
}

/// <summary>
/// Filters of the cross-raft log list; null means "any". A caller who reads only some zones passes them in <see cref="ZoneIds"/>. The period is on the visit date, inclusive.
/// <see cref="Sort"/> and <see cref="Descending"/> order the whole result; ties fall back to the recording time (latest first), then the id.
/// </summary>
public sealed record CultivationLogFilter(
    Guid? ZoneId, Guid? RaftId, IReadOnlySet<Guid>? ZoneIds, string? LogType, DateOnly? From, DateOnly? To, string? Search,
    CultivationLogSort Sort = CultivationLogSort.VisitDate, bool Descending = true);

public interface ICultivationLogRepository
{
    /// <summary>The log with its photos, raft, zone and cycle (null when it has none), tracked for editing.</summary>
    Task<CultivationLog?> GetForEditAsync(Guid logId, CancellationToken cancellationToken);

    /// <summary>The log with its photos, raft, zone and cycle (null when it has none), read only.</summary>
    Task<CultivationLog?> GetAsync(Guid logId, CancellationToken cancellationToken);

    /// <summary>The logs of a cycle with their photos, the latest visit first; only those of <paramref name="logType"/> when it is not null.</summary>
    Task<IReadOnlyList<CultivationLog>> ListByCycleAsync(Guid cycleId, string? logType, CancellationToken cancellationToken);

    /// <summary>The logs of a raft, in every cycle it had and in none, with their photos and cycle, the latest visit first; only those of <paramref name="logType"/> when it is not null.</summary>
    Task<IReadOnlyList<CultivationLog>> ListByRaftAsync(Guid raftId, string? logType, CancellationToken cancellationToken);

    /// <summary>One page of the logs that match, ordered by <see cref="CultivationLogFilter.Sort"/> (the latest visit first by default), with photos, cycle, raft and zone, and how many match: the count, the page, and one read for the photos of the page.</summary>
    Task<(IReadOnlyList<CultivationLog> Rows, int Total)> SearchAsync(CultivationLogFilter filter, int page, int pageSize, CancellationToken cancellationToken);

    /// <summary>Adds the log with the photos it holds.</summary>
    void Add(CultivationLog log);

    void AddPhoto(CultivationLogPhoto photo);

    void RemovePhoto(CultivationLogPhoto photo);

    /// <summary>Removes the log with the photos it holds; the foreign key does not cascade.</summary>
    void Remove(CultivationLog log);
}
