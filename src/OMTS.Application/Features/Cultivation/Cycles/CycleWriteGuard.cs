using OMTS.Application.Common.Messages;
using OMTS.Application.Common.Results;
using OMTS.Application.Features.Farming.Rafts;
using OMTS.Domain.Constants;
using OMTS.Domain.Entities.Cultivation;

namespace OMTS.Application.Features.Cultivation.Cycles;

/// <summary>Whether the records of a cycle may still change in the cultivation screens: the cycle is Open (BR-CYCLE-04) and its raft is not read-only (GBR-01).</summary>
public sealed class CycleWriteGuard(RaftViews raftViews)
{
    /// <summary>MSG-GEN-09 when the cycle is Closed or its raft is read-only, null when it may be changed. The cycle comes with its raft and zone.</summary>
    public AppError? Refusal(CultivationCycle cycle) => cycle.StatusId != StatusIds.CultivationCycle.Open
        ? AppError.Conflict(UserMessages.WrongStatus($"The cultivation cycle of raft {cycle.Raft.Code}", "Closed"))
        : raftViews.EditRefusal(cycle.Raft);
}
