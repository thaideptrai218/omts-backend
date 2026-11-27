using OMTS.Application.Common.Interfaces.Persistence;
using OMTS.Application.Common.Results;
using OMTS.Application.Features.Cultivation.MaterialUsages;
using OMTS.Application.Features.Cultivation.Progress;
using OMTS.Domain.Entities.Cultivation;
using OMTS.Domain.Entities.Farming;

namespace OMTS.Application.Features.Cultivation.CultivationLogs;

/// <summary>What a visit date resolves to: the Open cycle it falls in, no cycle, or a refusal because it falls inside a Closed cycle.</summary>
/// <param name="Cycle">The Open cycle, tracked for the save; null when the date is in no cycle or the date is refused.</param>
/// <param name="Refusal">MSG-GEN-09 (409) naming the Closed cycle; null when the date can be saved.</param>
public readonly record struct LogCycleResolution(CultivationCycle? Cycle, AppError? Refusal);

/// <summary>
/// The rule that gives a care log its cycle, the same as for material usage (BR-CYCLE-03): the cycle is read from the visit date and never chosen. A date inside the Open cycle of the
/// raft belongs to it, a date inside a Closed cycle is refused (BR-CYCLE-04) and a date in no cycle leaves the log without one. The row that guards the save is part of it: the cycle
/// row, so a parallel close, cancel or new start date fails with MSG06 or this save does; with no cycle, the raft row, as a parallel discontinue has nothing else in common with this save.
/// </summary>
public sealed class CultivationLogCycleResolver(ICultivationCycleQuery cycleQuery, ICultivationCycleRepository cycles, IUnitOfWork unitOfWork)
{
    /// <summary>The cycle of <paramref name="raft"/> on <paramref name="date"/>. The date is already checked not to be after today.</summary>
    public async Task<LogCycleResolution> ResolveAsync(Raft raft, DateOnly date, CancellationToken cancellationToken)
    {
        var span = await cycleQuery.CycleOnDateAsync(raft.Id, date, cancellationToken);
        if (span?.CloseDate is not null)
        {
            return new LogCycleResolution(null, MaterialUsageCycleGuard.Closed(raft, span));
        }

        // Read again for the save: the row may have been closed or cancelled since the look above.
        var cycle = span is null ? null : await cycles.GetForEditAsync(span.Id, cancellationToken);
        if (cycle is null)
        {
            unitOfWork.MarkChanged(raft);
            return new LogCycleResolution(null, null);
        }

        if (cycle.CloseDate is not null)
        {
            return new LogCycleResolution(null, MaterialUsageCycleGuard.Closed(raft, new CycleSpan(cycle.Id, cycle.RaftId, cycle.StartDate, cycle.CloseDate)));
        }

        unitOfWork.MarkChanged(cycle);
        return new LogCycleResolution(cycle, null);
    }
}
