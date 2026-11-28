using OMTS.Application.Common.Interfaces;
using OMTS.Application.Common.Interfaces.Persistence;
using OMTS.Application.Common.Messages;
using OMTS.Application.Common.Results;
using OMTS.Application.Features.Audit;
using OMTS.Application.Features.Audit.Models;
using OMTS.Domain.Entities.Farming;
using OMTS.Domain.Entities.Harvest;
using OMTS.Domain.Entities.Labor;
using OMTS.Domain.Enums;

namespace OMTS.Application.Features.WorkDayPlans;

/// <summary>
/// Each line of a harvest plan (KHT) has exactly one work-day plan of kind Harvest, made, moved and cancelled with the line: its day and time are the planned time of the harvest plan, its place the raft of the line, its
/// person in charge the Zone Manager of the raft's zone when the Zone Managers harvest (the Owner otherwise or when the zone has none). The harvest plan cannot be handed out ("Giao") until every one of its plans is full
/// (<see cref="GateAssign"/>). Nothing here saves: the harvest plan service saves with its own change.
/// </summary>
public sealed class WorkDayPlanHarvestLink(
    IWorkDayPlanRepository plans,
    WorkDayPlanCodeService codes,
    WorkDayPlanAccess access,
    WorkDayPlanStaffingService staffing,
    WorkDayPlanCrudService crud,
    WorkDayPlanSettler settler,
    ICurrentUser user,
    AuditLogService audit,
    IUnitOfWork unitOfWork)
{
    /// <summary>
    /// Makes the plan of every line that has none, and gives a line the number of people asked for (<paramref name="requiredByLine"/> by line number; a new plan starts with 1, which keeps it short until people are chosen).
    /// The lines come with their plan (<see cref="HarvestPlanLine.WorkDayPlan"/>) loaded.
    /// </summary>
    public async Task EnsurePlansAsync(HarvestPlan harvest, IReadOnlyDictionary<int, int?> requiredByLine, CancellationToken cancellationToken)
    {
        foreach (var line in harvest.Lines.OrderBy(l => l.LineNumber))
        {
            var asked = requiredByLine.GetValueOrDefault(line.LineNumber);
            if (line.WorkDayPlan is { } existing)
            {
                if (asked is { } people && people != existing.RequiredPeople && WorkDayPlanRules.Open(existing.Status))
                {
                    existing.RequiredPeople = people;
                    await settler.SettleAsync(existing, cancellationToken);
                }

                continue;
            }

            var responsible = await access.ResponsibleForHarvestAsync(line.Raft.ZoneId, harvest.Executor, user.AccountId, cancellationToken) ?? harvest.CreatedByAccountId;
            var plan = new WorkDayPlan
            {
                Code = await codes.NextAsync(cancellationToken),
                Kind = WorkDayPlanKind.Harvest,
                Status = WorkDayPlanStatus.Staffing,
                WorkDate = harvest.PlannedDate,
                StartsAt = harvest.PlannedAt,
                ZoneId = line.Raft.ZoneId,
                Zone = line.Raft.Zone,
                RaftId = line.RaftId,
                Raft = line.Raft,
                HarvestPlanLineId = line.Id,
                HarvestPlanLine = line,
                CreatorAccountId = user.AccountId ?? harvest.CreatedByAccountId,
                ResponsibleAccountId = responsible,
                RequiredPeople = asked ?? 1,
                ClientOperationId = Guid.NewGuid(),
            };
            plans.Add(plan);
            line.WorkDayPlan = plan;
            audit.Record(new AuditEntry("WorkDayPlan.Created", nameof(WorkDayPlan))
            {
                RecordId = plan.Id, RecordCode = plan.Code, ZoneId = plan.ZoneId, After = new { plan.Code, Kind = plan.Kind.ToString(), plan.WorkDate, plan.RaftId, plan.RequiredPeople, HarvestPlan = harvest.Code },
            });
        }
    }

    /// <summary>
    /// The plans carry the harvest plan's changes, checked before anything changes: a line kept moves to the new time (and to its new raft when it changed), with the people on it; a line taken out has its plan cancelled and its
    /// assignments removed (unless the person checked in). A refusal (someone checked in, has other work on the new day, the day or the pay period is closed) is the answer and leaves everything as it is.
    /// </summary>
    /// <param name="keptLines">Each line that stays, with the raft it has after the change.</param>
    public async Task<AppError?> FollowAsync(HarvestPlan harvest, DateTimeOffset newPlannedAt, DateOnly newDate, IReadOnlyDictionary<Guid, Raft> keptLines, CancellationToken cancellationToken)
    {
        var open = harvest.Lines.Where(l => l.WorkDayPlan is { } w && WorkDayPlanRules.Open(w.Status)).ToList();
        foreach (var line in open)
        {
            var plan = line.WorkDayPlan!;
            if (keptLines.TryGetValue(line.Id, out var raft) && await staffing.FollowPlaceAsync(plan, newDate, raft.ZoneId, raft.Id, cancellationToken) is { } refused)
            {
                return refused;
            }
        }

        foreach (var line in open)
        {
            var plan = line.WorkDayPlan!;
            if (keptLines.TryGetValue(line.Id, out var raft))
            {
                plan.WorkDate = newDate;
                plan.StartsAt = newPlannedAt;
                (plan.ZoneId, plan.Zone, plan.RaftId, plan.Raft) = (raft.ZoneId, raft.Zone, raft.Id, raft);
                unitOfWork.MarkChanged(plan);
                continue;
            }

            await staffing.ReleaseAssignmentsAsync(plan, cancellationToken);
            crud.Cancel(plan, $"Dòng bè {line.Raft.Code} đã bị bỏ khỏi kế hoạch thu hoạch {harvest.Code}.");
            plan.HarvestPlanLineId = null;
            plan.HarvestPlanLine = null;
        }

        return null;
    }

    /// <summary>The harvest plan is cancelled: its plans are cancelled with their assignments (removed unless the person checked in).</summary>
    public async Task CancelAsync(HarvestPlan harvest, string reason, CancellationToken cancellationToken)
    {
        foreach (var plan in harvest.Lines.Select(l => l.WorkDayPlan).OfType<WorkDayPlan>().Where(p => WorkDayPlanRules.Open(p.Status)))
        {
            await staffing.ReleaseAssignmentsAsync(plan, cancellationToken);
            crud.Cancel(plan, $"Kế hoạch thu hoạch {harvest.Code} đã hủy: {reason}");
        }
    }

    /// <summary>The harvest plan is handed out, so its person in charge is known: the Zone Manager of each raft's zone for the Zone Managers, the Owner for the Owner's own harvest.</summary>
    public async Task ApplyExecutorAsync(HarvestPlan harvest, CancellationToken cancellationToken)
    {
        foreach (var line in harvest.Lines)
        {
            if (line.WorkDayPlan is not { } plan || !WorkDayPlanRules.Open(plan.Status))
            {
                continue;
            }

            if (await access.ResponsibleForHarvestAsync(plan.ZoneId, harvest.Executor, user.AccountId, cancellationToken) is { } responsible && responsible != plan.ResponsibleAccountId)
            {
                plan.ResponsibleAccountId = responsible;
                unitOfWork.MarkChanged(plan);
            }
        }
    }

    /// <summary>"Giao" is refused (409 MSG-GEN-09, <c>reasonCode</c> PLAN_SHORT_OF_PEOPLE, the short plans in <c>plans</c>) while any plan of the harvest plan is short of people.</summary>
    public static AppError? GateAssign(HarvestPlan harvest)
    {
        var shortPlans = harvest.Lines.OrderBy(l => l.LineNumber)
            .Where(l => l.WorkDayPlan is { } w && WorkDayPlanRules.IsShort(w)).Select(l => (Line: l, Plan: l.WorkDayPlan!)).ToList();
        if (shortPlans.Count == 0)
        {
            return null;
        }

        var rafts = string.Join(", ", shortPlans.Select(s => s.Line.Raft.Code));
        return AppError.Conflict(
            UserMessages.WorkPlanRefused($"Không thể giao kế hoạch {harvest.Code}: bè {rafts} chưa đủ người."),
            new Dictionary<string, object?>
            {
                ["reasonCode"] = "PLAN_SHORT_OF_PEOPLE",
                ["plans"] = shortPlans.Select(s => new ShortWorkDayPlan(s.Plan.Id, s.Plan.Code, s.Line.Raft.Code, s.Plan.RequiredPeople, WorkDayPlanRules.Present(s.Plan))).ToList(),
            });
    }
}
