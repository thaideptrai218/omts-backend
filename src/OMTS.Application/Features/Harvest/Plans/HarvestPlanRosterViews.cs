using OMTS.Application.Common;
using OMTS.Application.Common.Concurrency;
using OMTS.Application.Common.Interfaces.Persistence;
using OMTS.Domain.Entities.Harvest;
using OMTS.Domain.Entities.Labor;

namespace OMTS.Application.Features.Harvest.Plans;

/// <summary>Builds the roster answers from a plan loaded with its roster (the people with their type periods).</summary>
public sealed class HarvestPlanRosterViews(IAccountRepository accounts, TimeProvider timeProvider)
{
    /// <summary>The roster with its numbers, ordered by the time each person was put on it. <paramref name="canEdit"/> is the caller's right to change it now.</summary>
    public async Task<HarvestPlanRoster> BuildAsync(HarvestPlan plan, bool canEdit, CancellationToken cancellationToken)
    {
        var confirmerIds = plan.Staff.Select(s => s.ConfirmedByAccountId).OfType<Guid>().Distinct().ToList();
        var names = confirmerIds.Count == 0 ? new Dictionary<Guid, string>() : await accounts.GetDisplayNamesAsync(confirmerIds, cancellationToken);
        var people = plan.Staff.OrderBy(s => s.InvitedAt).ThenBy(s => s.Employee.FullName).ThenBy(s => s.Id).Select(s => RowOf(s, names)).ToList();
        return new HarvestPlanRoster(
            plan.Id, plan.Code, RowVersionGuard.Encode(plan.RowVersion), HarvestPlanRosterRules.VersionOf(plan), plan.RequiredPeople, HarvestPlanRosterRules.Summary(plan), people, canEdit);
    }

    public HarvestPlanRosterRow RowOf(HarvestPlanStaffMember row, IReadOnlyDictionary<Guid, string>? confirmerNames = null)
    {
        var employee = row.Employee;
        return new HarvestPlanRosterRow(
            row.Id,
            row.EmployeeId,
            employee.Code,
            employee.FullName,
            employee.PhoneNumber,
            EmployeeTypePeriod.TypeOn(employee.TypePeriods, timeProvider.Today()),
            employee.AccountId is not null,
            row.State,
            row.InvitedAt,
            row.RespondedAt,
            row.Reason,
            row.ConfirmedByAccountId is { } id ? confirmerNames?.GetValueOrDefault(id) : null,
            RowVersionGuard.Encode(row.RowVersion));
    }
}
