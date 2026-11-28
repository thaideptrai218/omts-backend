using OMTS.Application.Common.Interfaces;
using OMTS.Application.Common.Interfaces.Persistence;
using OMTS.Application.Features.Audit;
using OMTS.Application.Features.Audit.Models;
using OMTS.Domain.Entities.Labor;

namespace OMTS.Application.Features.WorkDayPlans;

/// <summary>
/// Brings a plan's status in line with its people after a staffing change, in the unit of work of that change: audits the move, stamps the plan so its version follows the staffing, and sends the notices
/// (full, short again, invitations that did not succeed). Nothing is saved here.
/// </summary>
public sealed class WorkDayPlanSettler(WorkDayPlanNotifier notifier, AuditLogService audit, IUnitOfWork unitOfWork, ICurrentUser user, TimeProvider timeProvider)
{
    public async Task<WorkDayPlanSettlement> SettleAsync(WorkDayPlan plan, CancellationToken cancellationToken)
    {
        var settlement = WorkDayPlanRules.Settle(plan, timeProvider.GetUtcNow());
        unitOfWork.MarkChanged(plan);
        if (settlement.To != settlement.From)
        {
            audit.Record(new AuditEntry("WorkDayPlan.StatusChanged", nameof(WorkDayPlan))
            {
                RecordId = plan.Id,
                RecordCode = plan.Code,
                ZoneId = plan.ZoneId,
                Before = new { Status = settlement.From.ToString() },
                After = new { Status = settlement.To.ToString(), Present = WorkDayPlanRules.Present(plan), plan.RequiredPeople },
            });
        }

        if (settlement.BecameReady)
        {
            await notifier.ReadyAsync(plan, user.AccountId, cancellationToken);
        }
        else if (settlement.BecameShort)
        {
            await notifier.ShortAgainAsync(plan, user.AccountId, cancellationToken);
        }

        if (settlement.Unsuccessful.Count > 0)
        {
            foreach (var member in settlement.Unsuccessful)
            {
                audit.Record(new AuditEntry("WorkDayPlan.MemberUnsuccessful", nameof(WorkDayPlan))
                {
                    RecordId = plan.Id,
                    RecordCode = plan.Code,
                    ZoneId = plan.ZoneId,
                    After = new { Employee = member.Employee.FullName, State = member.State.ToString() },
                });
            }

            await notifier.UnsuccessfulAsync(plan, settlement.Unsuccessful.Select(m => m.Employee.AccountId).OfType<Guid>().ToList(), cancellationToken);
        }

        return settlement;
    }
}
