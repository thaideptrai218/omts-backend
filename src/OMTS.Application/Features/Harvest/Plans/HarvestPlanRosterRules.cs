using System.Security.Cryptography;
using System.Text;
using OMTS.Domain.Constants;
using OMTS.Domain.Entities.Harvest;
using OMTS.Domain.Enums;

namespace OMTS.Application.Features.Harvest.Plans;

/// <summary>The rules of the staffing roster of a harvest plan as pure functions, so they are unit tested and every endpoint gives one answer.</summary>
public static class HarvestPlanRosterRules
{
    public const int MinRequiredPeople = 1;
    public const int MaxRequiredPeople = 500;
    public const int MaxReasonLength = 500;

    /// <summary>The most people one request puts on a roster.</summary>
    public const int MaxPeoplePerRequest = 200;

    /// <summary>The roster is open until the plan is Completed or Cancelled; harvesting is not blocked by it (the owner's rule: warn, do not block).</summary>
    public static bool Open(int planStatusId) => planStatusId is not (StatusIds.HarvestPlan.Completed or StatusIds.HarvestPlan.Cancelled);

    /// <summary>The number needed is valid: absent, or 1 to 500.</summary>
    public static bool ValidRequiredPeople(int? requiredPeople) => requiredPeople is null or (>= MinRequiredPeople and <= MaxRequiredPeople);

    /// <summary>
    /// The numbers of a roster. Present people are the confirmed (accepted or confirmed by the Owner) and the external ones; the plan is ready when no number is set or the present people reach it.
    /// </summary>
    public static HarvestPlanStaffingSummary Summary(int? requiredPeople, IEnumerable<HarvestPlanStaffState> states)
    {
        var counts = states.GroupBy(s => s).ToDictionary(g => g.Key, g => g.Count());
        int Of(HarvestPlanStaffState state) => counts.GetValueOrDefault(state);
        var confirmed = Of(HarvestPlanStaffState.Confirmed) + Of(HarvestPlanStaffState.ConfirmedManually);
        var external = Of(HarvestPlanStaffState.External);
        var missing = requiredPeople is { } required ? Math.Max(0, required - confirmed - external) : 0;
        return new HarvestPlanStaffingSummary(requiredPeople, confirmed, Of(HarvestPlanStaffState.Invited), Of(HarvestPlanStaffState.Declined), external, missing, missing == 0);
    }

    public static HarvestPlanStaffingSummary Summary(HarvestPlan plan) => Summary(plan.RequiredPeople, plan.Staff.Select(s => s.State));

    /// <summary>
    /// Changes whenever the number needed or any row (its id, state or version) changes: the ETag of the roster, so a screen that polls every few seconds gets a 304 while nothing moved.
    /// </summary>
    public static string VersionOf(HarvestPlan plan)
    {
        var text = new StringBuilder().Append(plan.RequiredPeople).Append('|').Append(plan.StatusId);
        foreach (var row in plan.Staff.OrderBy(s => s.Id))
        {
            text.Append('|').Append(row.Id).Append(':').Append((int)row.State).Append(':').Append(Convert.ToHexString(row.RowVersion));
        }

        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text.ToString())))[..24];
    }

    /// <summary>
    /// Whether an employee may answer: while the plan is open, and never a person added outside the system (they count already). Answering again with the same answer is allowed (nothing changes);
    /// a person may change their mind while the plan is open.
    /// </summary>
    public static bool MayRespond(int planStatusId, HarvestPlanStaffState state) => Open(planStatusId) && state != HarvestPlanStaffState.External;
}
