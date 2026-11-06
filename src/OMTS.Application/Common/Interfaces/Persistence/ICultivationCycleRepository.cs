using OMTS.Domain.Entities.Cultivation;

namespace OMTS.Application.Common.Interfaces.Persistence;

public interface ICultivationCycleRepository
{
    /// <summary>The cycle with its raft and zone, tracked for editing.</summary>
    Task<CultivationCycle?> GetForEditAsync(Guid cycleId, CancellationToken cancellationToken);

    /// <summary>The cycle with its raft, read only.</summary>
    Task<CultivationCycle?> GetAsync(Guid cycleId, CancellationToken cancellationToken);

    /// <summary>The cycles of a raft, the newest first (CYCLE-05.1).</summary>
    Task<IReadOnlyList<CultivationCycle>> ListByRaftAsync(Guid raftId, CancellationToken cancellationToken);

    /// <summary>The cycles of every raft of a zone (SCR-CYCLE-01).</summary>
    Task<IReadOnlyList<CultivationCycle>> ListByZoneAsync(Guid zoneId, CancellationToken cancellationToken);

    /// <summary>The close date of the raft's last closed cycle, or null (BR-CYCLE-01).</summary>
    Task<DateOnly?> LastCloseDateAsync(Guid raftId, CancellationToken cancellationToken);

    /// <summary>The date of the first seeding session of the cycle, or null: a new start date must not be after it (BR-CYCLE-01).</summary>
    Task<DateOnly?> FirstRecordDateAsync(Guid cycleId, CancellationToken cancellationToken);

    /// <summary>Those of the cycles that have a record, so they can no longer be cancelled (BR-CYCLE-01).</summary>
    Task<IReadOnlySet<Guid>> WithRecordsAsync(IReadOnlyCollection<Guid> cycleIds, CancellationToken cancellationToken);

    void Add(CultivationCycle cycle);

    void Remove(CultivationCycle cycle);
}
