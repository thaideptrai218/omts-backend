using OMTS.Application.Common.Messages;
using OMTS.Application.Common.Results;
using OMTS.Application.Features.Farming.Rafts;
using OMTS.Domain.Constants;
using OMTS.Domain.Entities.Cultivation;
using OMTS.Domain.Entities.Farming;

namespace OMTS.Application.Features.Cultivation.CultivationLogs;

/// <summary>
/// Whether a care log may still change: its cycle, when it has one, is Open (BR-CYCLE-04), and its raft takes care records, which is a raft In Use or Discontinued in a zone In Use
/// (<see cref="RaftViews.AcceptsCareRecords"/>). A log in no cycle is never locked by a cycle. After the close, a log is corrected only in TRACE-03.
/// </summary>
public sealed class CultivationLogWriteGuard(RaftViews raftViews)
{
    /// <summary>MSG-GEN-09 when the log's cycle is Closed or its raft or zone cannot take records, null when it may be changed. The log comes with its cycle, raft and zone.</summary>
    public AppError? Refusal(CultivationLog log) =>
        log.Cycle is { } cycle && cycle.StatusId != StatusIds.CultivationCycle.Open ? Closed(log.Raft) : raftViews.CareRecordRefusal(log.Raft);

    /// <summary>MSG-GEN-09 when the cycle is not Open or its raft cannot take records, null when a log may be recorded in it. The cycle comes with its raft and zone.</summary>
    public AppError? Refusal(CultivationCycle cycle) =>
        cycle.StatusId != StatusIds.CultivationCycle.Open ? Closed(cycle.Raft) : raftViews.CareRecordRefusal(cycle.Raft);

    /// <summary>MSG-GEN-09 when the raft cannot take care records (<see cref="RaftViews.AcceptsCareRecords"/>), null when a log may be recorded on it. The raft comes with its zone.</summary>
    public AppError? Refusal(Raft raft) => raftViews.CareRecordRefusal(raft);

    private static AppError Closed(Raft raft) =>
        AppError.Conflict(UserMessages.WrongStatus($"The cultivation cycle of raft {raft.Code}", "Closed"));

    /// <summary>The caller may change the log now: <see cref="Refusal(CultivationLog)"/> is null.</summary>
    public static bool MayChange(CultivationLog log) =>
        (log.Cycle is null || log.Cycle.StatusId == StatusIds.CultivationCycle.Open) && RaftViews.AcceptsCareRecords(log.Raft);
}
