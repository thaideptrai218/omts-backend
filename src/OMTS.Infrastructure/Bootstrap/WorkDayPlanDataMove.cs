using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using OMTS.Application.Common;
using OMTS.Application.Common.Constants;
using OMTS.Application.Common.Interfaces.Persistence;
using OMTS.Application.Features.Harvest.Plans;
using OMTS.Application.Features.WorkDayPlans;
using OMTS.Domain.Constants;
using OMTS.Domain.Entities.Harvest;
using OMTS.Domain.Entities.Labor;
using OMTS.Domain.Enums;
using OMTS.Infrastructure.Persistence;

namespace OMTS.Infrastructure.Bootstrap;

/// <summary>
/// The harvest plans (KHT) that are still open get the work-day plans of their lines: one plan per line, with the people already chosen. The roster of the harvest plan (the people invited, who answered, and the ones
/// hired on the spot) becomes the members of the plan of its first line, the people hired on the spot being confirmed on behalf with the reason "chuyển dữ liệu"; the number of people the harvest plan needed goes to
/// that plan, and every other plan needs the people it has, at least 1. The assignments made for a line (the old Nhân công of a line) are linked to the plan of the line and their people become members when they are not
/// yet. A line that is done has its plan Completed. The older work-day plans are moved by the migration. The step is idempotent: a line that has its plan is not made again, an assignment already linked is left, and
/// it never fails the deploy. The old tables stay until the endpoints that write them are removed.
/// </summary>
public sealed class WorkDayPlanDataMove(
    OmtsDbContext db, IHarvestResponsibleLookup responsibles, IDocumentNumberAllocator numbers, TimeProvider timeProvider, ILogger<WorkDayPlanDataMove> logger)
{
    private const string MovedReason = "chuyển dữ liệu";

    private static readonly int[] OpenStatuses =
        [StatusIds.HarvestPlan.Draft, StatusIds.HarvestPlan.Assigned, StatusIds.HarvestPlan.Received, StatusIds.HarvestPlan.Harvesting, StatusIds.HarvestPlan.HandedOver];

    public async Task<string> RunAsync(CancellationToken cancellationToken)
    {
        try
        {
            var made = 0;
            var linked = 0;
            var harvests = await db.HarvestPlans
                .Include(p => p.Lines).ThenInclude(l => l.Raft)
                .Include(p => p.Lines).ThenInclude(l => l.Lanes)
                .Include(p => p.Lines).ThenInclude(l => l.Lot)
                .Include(p => p.Staff)
                .Where(p => OpenStatuses.Contains(p.StatusId))
                .OrderBy(p => p.Code)
                .ToListAsync(cancellationToken);
            foreach (var harvest in harvests)
            {
                var lineIds = harvest.Lines.Select(l => l.Id).ToList();
                var existing = (await db.WorkDayPlans.Include(w => w.Members).Include(w => w.OutsiderHires).Where(w => w.HarvestPlanLineId != null && lineIds.Contains(w.HarvestPlanLineId.Value)).ToListAsync(cancellationToken))
                    .ToDictionary(w => w.HarvestPlanLineId!.Value);
                var assignments = await db.WorkAssignments.Where(a => a.HarvestPlanLineId != null && lineIds.Contains(a.HarvestPlanLineId.Value) && a.WorkDayPlanId == null)
                    .ToListAsync(cancellationToken);
                var first = true;
                foreach (var line in harvest.Lines.OrderBy(l => l.LineNumber))
                {
                    var lineAssignments = assignments.Where(a => a.HarvestPlanLineId == line.Id).ToList();
                    if (!existing.TryGetValue(line.Id, out var plan))
                    {
                        plan = await NewPlanAsync(harvest, line, first, lineAssignments.Count, cancellationToken);
                        db.WorkDayPlans.Add(plan);
                        if (first)
                        {
                            AddRoster(plan, harvest);
                        }

                        made++;
                    }

                    first = false;
                    linked += Link(plan, harvest, lineAssignments);
                    var now = timeProvider.GetUtcNow();
                    WorkDayPlanRules.Settle(plan, now);
                    WorkDayPlanRules.FollowHarvestLine(plan, HarvestPlanRules.LineDone(HarvestPlanRules.FactsOf(line)), now);
                }
            }

            if (made == 0 && linked == 0)
            {
                return "Work-day plans of open harvest plans: nothing to move.";
            }

            await db.SaveChangesAsync(cancellationToken);
            return $"Work-day plans of open harvest plans: {made} plan(s) made, {linked} assignment(s) linked.";
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            logger.LogWarning(exception, "The work-day plans of the open harvest plans could not be made; the next deploy retries.");
            return "Work-day plans of open harvest plans: skipped after an error (see the log).";
        }
    }

    private async Task<WorkDayPlan> NewPlanAsync(HarvestPlan harvest, HarvestPlanLine line, bool first, int assigned, CancellationToken cancellationToken)
    {
        var responsible = await responsibles.ResponsibleForHarvestAsync(line.Raft.ZoneId, harvest.Executor, harvest.CreatedByAccountId, cancellationToken) ?? harvest.CreatedByAccountId;
        // The number the harvest plan needed goes to its first plan; the others need the people they have.
        var people = first ? harvest.RequiredPeople ?? Math.Max(1, harvest.Staff.Count(s => s.CountsAsPresent)) : assigned;
        return new WorkDayPlan
        {
            Code = WorkDayPlanCodeService.Format(await numbers.NextAsync(DocumentSequenceKeys.WorkDayPlan, cancellationToken)),
            Kind = WorkDayPlanKind.Harvest,
            Status = WorkDayPlanStatus.Staffing,
            WorkDate = harvest.PlannedDate,
            StartsAt = harvest.PlannedAt,
            ZoneId = line.Raft.ZoneId,
            RaftId = line.RaftId,
            HarvestPlanLineId = line.Id,
            CreatorAccountId = harvest.CreatedByAccountId,
            ResponsibleAccountId = responsible,
            RequiredPeople = Math.Clamp(people, WorkDayPlanRules.MinRequiredPeople, WorkDayPlanRules.MaxRequiredPeople),
            ClientOperationId = Guid.NewGuid(),
        };
    }

    /// <summary>The roster of the harvest plan becomes the members of its first plan; the people hired on the spot are confirmed on behalf.</summary>
    private void AddRoster(WorkDayPlan plan, HarvestPlan harvest)
    {
        var now = timeProvider.GetUtcNow();
        foreach (var row in harvest.Staff.OrderBy(s => s.InvitedAt))
        {
            var confirmedBy = row.ConfirmedByAccountId ?? row.AddedByAccountId ?? harvest.CreatedByAccountId;
            var member = new WorkDayPlanMember
            {
                Plan = plan,
                EmployeeId = row.EmployeeId,
                InvitedAt = row.InvitedAt,
                RespondedAt = row.RespondedAt,
                AddedByAccountId = row.AddedByAccountId,
            };
            switch (row.State)
            {
                case HarvestPlanStaffState.Invited:
                    // Still waiting: it gets the usual time to answer from now on.
                    (member.State, member.Deadline) = (WorkDayMemberState.Invited, now.AddMinutes(WorkDayPlanRules.DefaultDeadlineMinutes));
                    break;
                case HarvestPlanStaffState.Confirmed:
                    member.State = WorkDayMemberState.Accepted;
                    break;
                case HarvestPlanStaffState.ConfirmedManually:
                    (member.State, member.Reason, member.ConfirmedByAccountId) = (WorkDayMemberState.ConfirmedOnBehalf, row.Reason ?? MovedReason, confirmedBy);
                    break;
                case HarvestPlanStaffState.Declined:
                    (member.State, member.Reason) = (WorkDayMemberState.Declined, row.Reason);
                    break;
                default:
                    (member.State, member.Reason, member.ConfirmedByAccountId, member.RespondedAt) = (WorkDayMemberState.ConfirmedOnBehalf, MovedReason, confirmedBy, row.RespondedAt ?? row.InvitedAt);
                    break;
            }

            plan.Members.Add(member);
        }
    }

    /// <summary>The assignments of the line go to its plan; a person with none yet on the plan becomes a member confirmed on behalf. Returns how many assignments were linked.</summary>
    private int Link(WorkDayPlan plan, HarvestPlan harvest, IReadOnlyList<WorkAssignment> lineAssignments)
    {
        foreach (var assignment in lineAssignments)
        {
            assignment.WorkDayPlanId = plan.Id;
            var member = plan.Members.FirstOrDefault(m => m.EmployeeId == assignment.EmployeeId);
            if (member is null)
            {
                member = new WorkDayPlanMember
                {
                    Plan = plan,
                    EmployeeId = assignment.EmployeeId,
                    State = WorkDayMemberState.ConfirmedOnBehalf,
                    InvitedAt = assignment.CreatedAt,
                    RespondedAt = assignment.CreatedAt,
                    Reason = MovedReason,
                    AddedByAccountId = assignment.AssignedByAccountId,
                    ConfirmedByAccountId = assignment.AssignedByAccountId,
                };
                plan.Members.Add(member);
            }
            else if (member.State is WorkDayMemberState.Invited or WorkDayMemberState.Declined)
            {
                // The person has the work already: the roster is behind the assignment.
                (member.State, member.Reason, member.ConfirmedByAccountId, member.RespondedAt) =
                    (WorkDayMemberState.ConfirmedOnBehalf, MovedReason, assignment.AssignedByAccountId, timeProvider.GetUtcNow());
            }

            member.WorkAssignmentId = assignment.Id;
        }

        return lineAssignments.Count;
    }
}
