using OMTS.Application.Common.Interfaces.Persistence;
using OMTS.Application.Features.Audit;
using OMTS.Application.Features.Audit.Models;
using OMTS.Domain.Entities.Labor;

namespace OMTS.Application.Features.WorkDayPlans;

/// <summary>
/// The invitations that were not answered before their deadline end as Expired, and the person in charge is told "còn thiếu n" while the plan is still short. A hosted job runs it every minute: one plan at a time
/// in a unit of work of its own, so a plan changed meanwhile loses only its own save and is read again at the next run. Running it twice expires nothing more.
/// </summary>
public sealed class WorkDayPlanExpiryService(
    IWorkDayPlanRepository plans, WorkDayPlanNotifier notifier, AuditLogService audit, IUnitOfWork unitOfWork, TimeProvider timeProvider)
{
    /// <summary>The plans that have an invitation past its deadline.</summary>
    public Task<IReadOnlyList<Guid>> OverduePlanIdsAsync(CancellationToken cancellationToken) =>
        plans.ListPlanIdsWithOverdueInvitationsAsync(timeProvider.GetUtcNow(), cancellationToken);

    /// <summary>Expires the overdue invitations of the plan; returns how many ended.</summary>
    public async Task<int> ExpirePlanAsync(Guid planId, CancellationToken cancellationToken)
    {
        var plan = await plans.GetForEditAsync(planId, cancellationToken);
        if (plan is null)
        {
            return 0;
        }

        var expired = WorkDayPlanRules.ExpireOverdue(plan, timeProvider.GetUtcNow());
        if (expired.Count == 0)
        {
            return 0;
        }

        unitOfWork.MarkChanged(plan);
        foreach (var member in expired)
        {
            audit.Record(new AuditEntry("WorkDayPlan.MemberExpired", nameof(WorkDayPlan))
            {
                RecordId = plan.Id, RecordCode = plan.Code, ZoneId = plan.ZoneId, After = new { Employee = member.Employee.FullName, State = member.State.ToString() },
            });
        }

        if (WorkDayPlanRules.Open(plan.Status) && WorkDayPlanRules.Missing(plan) > 0)
        {
            await notifier.ExpiredAsync(plan, expired.Count, cancellationToken);
        }

        await unitOfWork.SaveChangesAsync(cancellationToken);
        return expired.Count;
    }
}
