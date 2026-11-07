using OMTS.Application.Common.Messages;
using OMTS.Application.Common.Results;
using OMTS.Application.Features.Farming.Rafts;
using OMTS.Domain.Constants;
using OMTS.Domain.Entities.Cultivation;

namespace OMTS.Application.Features.Cultivation.Cycles;

/// <summary>
/// Whether the records of a cycle may still change in the cultivation screens: the cycle is Open (BR-CYCLE-04) and its raft is not read-only (GBR-01). Every CYCLE-01 to CYCLE-06
/// write and every HARVEST-01.1 correction or removal asks it, so after the close (CYCLE-06) they all answer MSG-GEN-09.
/// Correct traceability data (TRACE-03) is the only use case that may still change the seeding sessions, logs and harvest data of a Closed cycle (BR-TRACE-04). It does not relax
/// this guard: it has its own (<c>Traceability.Corrections.TraceCorrectionGuard</c>: cycle Closed, or a session whose lanes are in a lot) and its own services, and keeps marking the
/// cycle row changed (report §4.3).
/// </summary>
public sealed class CycleWriteGuard(RaftViews raftViews)
{
    /// <summary>MSG-GEN-09 when the cycle is Closed or its raft is read-only, null when it may be changed. The cycle comes with its raft and zone.</summary>
    public AppError? Refusal(CultivationCycle cycle) => cycle.StatusId != StatusIds.CultivationCycle.Open
        ? AppError.Conflict(UserMessages.WrongStatus($"The cultivation cycle of raft {cycle.Raft.Code}", "Closed"))
        : raftViews.EditRefusal(cycle.Raft);
}
