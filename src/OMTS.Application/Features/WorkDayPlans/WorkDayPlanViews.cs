using OMTS.Application.Features.Authorization;
using OMTS.Application.Common;
using OMTS.Application.Common.Concurrency;
using OMTS.Application.Common.Interfaces.Persistence;
using OMTS.Application.Features.Authorization.Models;
using OMTS.Domain.Entities.Labor;
using OMTS.Domain.Enums;

namespace OMTS.Application.Features.WorkDayPlans;

/// <summary>Builds the answers of the work-day plans, with the buttons the caller may press.</summary>
public sealed class WorkDayPlanViews(IAccountRepository accounts, IWorkDayPlanRepository plans, WorkDayPlanAccess access, TimeProvider timeProvider)
{
    /// <summary>The plan with its members and outsider lines.</summary>
    public async Task<WorkDayPlanItem> DetailAsync(WorkDayPlan plan, CancellationToken cancellationToken) =>
        (await BuildAsync([plan], true, cancellationToken))[0];

    /// <summary>The plans as list rows (without members and outsider lines).</summary>
    public Task<IReadOnlyList<WorkDayPlanItem>> ListAsync(IReadOnlyList<WorkDayPlan> list, CancellationToken cancellationToken) => BuildAsync(list, false, cancellationToken);

    private async Task<IReadOnlyList<WorkDayPlanItem>> BuildAsync(IReadOnlyList<WorkDayPlan> list, bool detail, CancellationToken cancellationToken)
    {
        var who = await access.CurrentAsync(cancellationToken);
        var names = new Dictionary<Guid, string>();
        var vouchers = new Dictionary<Guid, string>();
        if (detail)
        {
            var ids = list.SelectMany(p => p.Members.Select(m => m.ConfirmedByAccountId).Concat(p.OutsiderHires.Select(h => h.PaidByAccountId))).OfType<Guid>().Distinct().ToList();
            if (ids.Count > 0)
            {
                names = (await accounts.GetDisplayNamesAsync(ids, cancellationToken)).ToDictionary(x => x.Key, x => x.Value);
            }

            vouchers = (await plans.VoucherCodesAsync(list.SelectMany(p => p.OutsiderHires).Select(h => h.CashVoucherId).OfType<Guid>().ToList(), cancellationToken))
                .ToDictionary(x => x.Key, x => x.Value);
        }

        var today = timeProvider.Today();
        return list.Select(p => ViewOf(p, who, detail, names, vouchers, today)).ToList();
    }

    private static WorkDayPlanItem ViewOf(
        WorkDayPlan p, AccountAccess? who, bool detail, IReadOnlyDictionary<Guid, string> names, IReadOnlyDictionary<Guid, string> vouchers, DateOnly today)
    {
        var confirmed = p.Members.Count(m => m.IsConfirmed);
        var outsiders = p.OutsiderHires.Sum(h => h.People);
        var present = confirmed + outsiders;
        var open = WorkDayPlanRules.Open(p.Status);
        var mayEdit = who is not null && WorkDayPlanAccess.MayEdit(who, p);
        var mayStaff = who is not null && WorkDayPlanAccess.MayStaff(who, p);
        var allowed = new WorkDayPlanAllowed(
            CanEdit: open && mayEdit,
            CanCancel: open && who is not null && WorkDayPlanAccess.MayCancel(who, p),
            CanComplete: p.Status is WorkDayPlanStatus.Ready or WorkDayPlanStatus.InProgress && p.WorkDate <= today && p.Kind != WorkDayPlanKind.Harvest && mayStaff,
            CanStaff: open && mayStaff,
            CanSetRequiredPeople: open && mayEdit,
            CanPayOutsiders: open && mayStaff && who is not null && WorkDayPlanAccess.MayPayOutsiders(who));
        return new WorkDayPlanItem(
            p.Id, p.Code, p.Kind, p.Status, p.WorkDate, p.StartsAt, p.ExpectedEndAt,
            new WorkDayPlanZoneRef(p.Zone.Id, p.Zone.Code, p.Zone.Name),
            p.Raft is { } raft ? new WorkDayPlanRaftRef(raft.Id, raft.Code) : null,
            p.NurseryBatch is { } batch ? new WorkDayPlanBatchRef(batch.Id, batch.Code) : null,
            p.HarvestPlanLine is { } line ? new WorkDayPlanHarvestRef(line.PlanId, line.Plan.Code, line.Id, line.LineNumber) : null,
            new WorkDayPlanAccountRef(p.ResponsibleAccountId, p.ResponsibleAccount.DisplayName),
            p.CreatorAccount.DisplayName,
            p.RequiredPeople,
            new WorkDayPlanStaffingView(p.RequiredPeople, confirmed, outsiders, present, Math.Max(0, p.RequiredPeople - present), p.Members.Count(m => m.State == WorkDayMemberState.Invited), present >= p.RequiredPeople),
            p.Note, p.CompletedAt, p.CancelledAt, p.CancelReason, allowed, p.CreatedAt, RowVersionGuard.Encode(p.RowVersion),
            detail ? p.Members.OrderBy(m => m.Employee.FullName, StringComparer.CurrentCultureIgnoreCase).ThenBy(m => m.Id).Select(m => MemberOf(m, names)).ToList() : null,
            detail ? p.OutsiderHires.OrderBy(h => h.CreatedAt).ThenBy(h => h.Id).Select(h => OutsiderOf(h, names, vouchers)).ToList() : null);
    }

    private static WorkDayMemberView MemberOf(WorkDayPlanMember m, IReadOnlyDictionary<Guid, string> names) => new(
        m.Id, m.EmployeeId, m.Employee.Code, m.Employee.FullName, m.Employee.PhoneNumber, m.State, m.InvitedAt, m.Deadline, m.RespondedAt, m.Reason,
        m.ConfirmedByAccountId is { } by ? names.GetValueOrDefault(by) : null, m.Employee.AccountId is not null, m.WorkAssignmentId, RowVersionGuard.Encode(m.RowVersion));

    private static PlanOutsiderHireView OutsiderOf(PlanOutsiderHire h, IReadOnlyDictionary<Guid, string> names, IReadOnlyDictionary<Guid, string> vouchers) => new(
        h.Id, h.People, h.RatePerSession, h.Amount, h.PayeeName, h.Note, h.IsPaid, h.CashVoucherId, h.CashVoucherId is { } v ? vouchers.GetValueOrDefault(v) : null,
        h.PaidAt, h.PaidByAccountId is { } by ? names.GetValueOrDefault(by) : null, RowVersionGuard.Encode(h.RowVersion));
}
