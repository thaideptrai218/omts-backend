using OMTS.Application.Common.Interfaces.Persistence;
using OMTS.Application.Common.Messages;
using OMTS.Application.Common.Results;
using OMTS.Application.Features.Cultivation.Progress;
using OMTS.Domain.Entities.Farming;

namespace OMTS.Application.Features.Cultivation.MaterialUsages;

/// <summary>
/// Whether a date of a raft is free for material usage: a date inside a closed cycle is refused and a record dated inside one is locked, the close day included
/// (BR-CYCLE-03, BR-CYCLE-04). The cycle is read from the date and never stored on the record.
/// </summary>
public sealed class MaterialUsageCycleGuard(ICultivationCycleQuery cycleQuery, ICultivationCycleRepository cycles, IUnitOfWork unitOfWork)
{
    /// <summary>MSG-GEN-09 naming the closed cycle the date falls in, null when there is none. Changes nothing.</summary>
    public async Task<AppError?> RefusalAsync(Raft raft, DateOnly date, CancellationToken cancellationToken) =>
        await cycleQuery.CycleOnDateAsync(raft.Id, date, cancellationToken) is { CloseDate: not null } closed ? Closed(raft, closed) : null;

    /// <summary>
    /// The same check, and it makes the guarding row part of this save. On a date inside an Open cycle that is the cycle row: a parallel close, cancel or new start date then
    /// fails with MSG06, or this save does. A raft with an Open cycle cannot be discontinued, so its row is left alone and its open forms stay valid. On a date with no cycle
    /// it is the raft row, as a parallel discontinue has nothing else in common with this save.
    /// </summary>
    public async Task<AppError?> HoldAsync(Raft raft, DateOnly date, CancellationToken cancellationToken)
    {
        var span = await cycleQuery.CycleOnDateAsync(raft.Id, date, cancellationToken);
        if (span?.CloseDate is not null)
        {
            return Closed(raft, span);
        }

        // Read again for the save: the row may have been closed or cancelled since the look above.
        var cycle = span is null ? null : await cycles.GetForEditAsync(span.Id, cancellationToken);
        if (cycle is null)
        {
            unitOfWork.MarkChanged(raft);
            return null;
        }

        if (cycle.CloseDate is not null)
        {
            return Closed(raft, new CycleSpan(cycle.Id, cycle.RaftId, cycle.StartDate, cycle.CloseDate));
        }

        unitOfWork.MarkChanged(cycle);
        return null;
    }

    private static AppError Closed(Raft raft, CycleSpan cycle) => AppError.Conflict(
        UserMessages.WrongStatus($"The cultivation cycle of raft {raft.Code} from {cycle.StartDate:dd/MM/yyyy} to {cycle.CloseDate:dd/MM/yyyy}", "Closed"),
        new Dictionary<string, object?> { ["cycleId"] = cycle.Id });
}
