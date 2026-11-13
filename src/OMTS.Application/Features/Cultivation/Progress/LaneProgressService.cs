using System.Globalization;
using OMTS.Application.Common;
using OMTS.Application.Common.Concurrency;
using OMTS.Application.Common.Interfaces;
using OMTS.Application.Common.Interfaces.Persistence;
using OMTS.Application.Common.Messages;
using OMTS.Application.Common.Results;
using OMTS.Application.Features.Audit;
using OMTS.Application.Features.Audit.Models;
using OMTS.Application.Features.Authorization;
using OMTS.Application.Features.Authorization.Models;
using OMTS.Application.Features.Farming;
using OMTS.Application.Features.Farming.Rafts;
using OMTS.Domain.Constants;
using OMTS.Domain.Entities.Cultivation;

namespace OMTS.Application.Features.Cultivation.Progress;

/// <summary>
/// Change the seeded or harvested percentage of a lane by hand (<c>PUT /api/rafts/{raftId}/lanes/{n}/progress</c>, owner decision of 12/11/2026). Send at least one percentage (0 to 100, two decimals),
/// the <paramref name="Reason"/> and the <paramref name="RowVersion"/> of the Open cycle.
/// </summary>
public sealed record LaneProgressRequest(decimal? SeededPercent, decimal? HarvestedPercent, string? Reason, string? RowVersion);

/// <summary>
/// The lane as it is after the change, in the shape of the lane reads. <paramref name="RowVersion"/> is the new version of the Open cycle (send it with the next change).
/// <paramref name="Changed"/> is false when the lane already held these percentages: nothing was saved.
/// </summary>
public sealed record LaneProgressSaved(
    int LaneNumber,
    decimal SeededPercent,
    decimal? HarvestedPercent,
    LaneState State,
    string RowVersion,
    string? LastModifiedByName,
    DateTimeOffset? LastModifiedAt,
    IReadOnlyList<LaneProgressChangeView> ProgressHistory,
    bool Changed,
    Guid CycleId,
    string Message,
    string MessageCode);

/// <summary>
/// The manual change of a lane's percentages, for a Zone Manager of the raft's zone and the Cooperative Owner, while the cycle is Open and the lane has seed in it. It is a correction that wins over the sessions and
/// lots recorded before it (<see cref="LanePercentRules"/>), kept as a <see cref="LaneProgressChange"/> with the totals before and after, who, when and why; the audit log gets the same, and the Owner is told when a
/// Zone Manager did it. The cycle row is part of the save, as the lane states depend on it: a parallel session, lot or change loses with MSG06.
/// </summary>
public sealed class LaneProgressService(
    IRaftRepository rafts,
    ICultivationCycleRepository cycles,
    ICultivationCycleQuery cycleQuery,
    ILaneProgressRepository progress,
    LaneStateReader laneStates,
    LaneProgressViews views,
    RaftViews raftViews,
    LaneIncidentNotifier notifier,
    ZoneAccessGuard zoneAccess,
    RowVersionGuard rowVersions,
    AuditLogService audit,
    IUnitOfWork unitOfWork,
    ICurrentUser currentUser,
    TimeProvider timeProvider)
{
    public const string SeededField = "Seeded percent";
    public const string HarvestedField = "Harvested percent";
    public const string ReasonField = "Reason";
    public const string ChangedAction = "Lane.ProgressChanged";

    public async Task<Result<LaneProgressSaved>> ChangeAsync(Guid raftId, int laneNumber, LaneProgressRequest request, CancellationToken cancellationToken)
    {
        var raft = await rafts.GetForEditAsync(raftId, cancellationToken);
        if (raft is null || raft.Lanes.FirstOrDefault(l => l.LaneNumber == laneNumber) is not { } lane)
        {
            return AppError.NotFound;
        }

        if (!await zoneAccess.AllowsAsync(PermissionFunctions.CultivationCycles, PermissionAction.Edit, raft.ZoneId, cancellationToken))
        {
            return AppError.Forbidden;
        }

        var check = new FieldCheck();
        check.Check(request.SeededPercent is not null || request.HarvestedPercent is not null, SeededField);
        check.Check(request.SeededPercent is null || IsPercent(request.SeededPercent.Value), SeededField);
        check.Check(request.HarvestedPercent is null || IsPercent(request.HarvestedPercent.Value), HarvestedField);
        var reason = check.Required(request.Reason, ReasonField, LaneProgressChange.MaxReasonLength);
        if (!check.IsValid)
        {
            return check.ToError();
        }

        if (lane.StatusId != StatusIds.Lane.InUse)
        {
            return AppError.Conflict(UserMessages.WrongStatus($"Lane {lane.LaneNumber} of raft {raft.Code}", "Removed"));
        }

        if (lane.IsOutOfService)
        {
            return Refused($"Lane {lane.LaneNumber} of raft {raft.Code}", "out of service", "LANE_OUT_OF_SERVICE", lane.LaneNumber);
        }

        if (raftViews.EditRefusal(raft) is { } readOnly)
        {
            return readOnly;
        }

        var span = await cycleQuery.OpenCycleAsync(raft.Id, cancellationToken);
        var cycle = span is null ? null : await cycles.GetForEditAsync(span.Id, cancellationToken);
        if (cycle is not { StatusId: StatusIds.CultivationCycle.Open })
        {
            return AppError.Conflict(UserMessages.WrongStatus($"Raft {raft.Code}", "without an Open cycle"));
        }

        if (await rowVersions.CheckAsync(cycle, request.RowVersion, cancellationToken) is { } conflict)
        {
            return conflict;
        }

        var today = timeProvider.Today();
        var day = await laneStates.ReadAsync(raft, today, cancellationToken);
        if (day.States[lane.Id] == LaneState.NotSeeded)
        {
            return Refused($"Lane {lane.LaneNumber} of raft {raft.Code}", "not seeded in this cycle", "LANE_NOT_SEEDED", lane.LaneNumber);
        }

        var current = day.Percents?.GetValueOrDefault(lane.Id) ?? new LanePercents(0m, 0m);
        var next = new LanePercents(request.SeededPercent ?? current.Seeded, request.HarvestedPercent ?? current.Harvested);
        if (next == current)
        {
            return await SavedAsync(raft, lane, cycle, false, cancellationToken);
        }

        var actor = currentUser.AccountId ?? throw new InvalidOperationException("A lane percentage change needs an authenticated caller.");
        progress.Add(new LaneProgressChange
        {
            CycleId = cycle.Id,
            LaneId = lane.Id,
            OccurredAt = timeProvider.GetUtcNow(),
            OccurredOn = today,
            ChangedByAccountId = actor,
            Reason = reason,
            OldSeededPercent = current.Seeded,
            OldHarvestedPercent = current.Harvested,
            SeededPercent = next.Seeded,
            HarvestedPercent = next.Harvested,
        });
        unitOfWork.MarkChanged(cycle);
        audit.Record(new AuditEntry(ChangedAction, LaneIncidentAudit.RecordType)
        {
            RecordId = lane.Id,
            RecordCode = $"{raft.Code}/{lane.LaneNumber}",
            ZoneId = raft.ZoneId,
            Reason = reason,
            Before = new { SeededPercent = current.Seeded, HarvestedPercent = current.Harvested },
            After = new { SeededPercent = next.Seeded, HarvestedPercent = next.Harvested },
        });
        await notifier.ProgressChangedAsync(raft, lane, Describe(current, next), reason, actor, cancellationToken);
        await unitOfWork.SaveChangesAsync(cancellationToken);
        return await SavedAsync(raft, lane, cycle, true, cancellationToken);
    }

    private static bool IsPercent(decimal value) => value is >= 0m and <= LanePercentRules.Whole && decimal.Round(value, 2) == value;

    private static AppError Refused(string subject, string status, string reasonCode, int laneNumber) =>
        AppError.Conflict(UserMessages.WrongStatus(subject, status), new Dictionary<string, object?> { ["reasonCode"] = reasonCode, ["laneNumber"] = laneNumber });

    private static string Describe(LanePercents from, LanePercents to)
    {
        static string Show(decimal? value) => value is { } v ? $"{v.ToString("0.##", CultureInfo.InvariantCulture)}%" : "not entered";
        var parts = new List<string>();
        if (from.Seeded != to.Seeded)
        {
            parts.Add($"seeded {Show(from.Seeded)} to {Show(to.Seeded)}");
        }

        if (from.Harvested != to.Harvested)
        {
            parts.Add($"harvested {Show(from.Harvested)} to {Show(to.Harvested)}");
        }

        return string.Join(", ", parts);
    }

    private async Task<LaneProgressSaved> SavedAsync(Domain.Entities.Farming.Raft raft, Domain.Entities.Farming.Lane lane, CultivationCycle cycle, bool changed, CancellationToken cancellationToken)
    {
        var day = await laneStates.ReadAsync(raft, timeProvider.Today(), cancellationToken);
        var info = (await views.InfoAsync(day, cancellationToken))[lane.Id];
        var saved = UserMessages.Saved("Lane progress");
        return new LaneProgressSaved(
            lane.LaneNumber, info.SeededPercent, info.HarvestedPercent, day.States[lane.Id], RowVersionGuard.Encode(cycle.RowVersion),
            info.LastModifiedByName, info.LastModifiedAt, info.ProgressHistory, changed, cycle.Id, saved.Text, saved.Code);
    }
}
