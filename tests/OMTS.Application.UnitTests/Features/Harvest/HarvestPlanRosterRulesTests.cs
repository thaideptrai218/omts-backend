using OMTS.Application.Features.Harvest.Plans;
using OMTS.Domain.Constants;
using OMTS.Domain.Entities.Harvest;
using OMTS.Domain.Enums;

namespace OMTS.Application.UnitTests.Features.Harvest;

/// <summary>The rules of the staffing roster of a harvest plan (owner decisions of 26/11/2026).</summary>
public class HarvestPlanRosterRulesTests
{
    [Fact]
    public void Confirmed_people_and_external_people_count_as_present_and_invited_and_declined_do_not()
    {
        var summary = HarvestPlanRosterRules.Summary(5, [
            HarvestPlanStaffState.Confirmed, HarvestPlanStaffState.ConfirmedManually, HarvestPlanStaffState.External,
            HarvestPlanStaffState.Invited, HarvestPlanStaffState.Invited, HarvestPlanStaffState.Declined]);

        Assert.Equal((5, 2, 2, 1, 1, 2, false), (summary.RequiredPeople, summary.Confirmed, summary.Invited, summary.Declined, summary.External, summary.Missing, summary.Ready));
    }

    [Theory]
    [InlineData(3, 3, true)]
    [InlineData(3, 4, true)]
    [InlineData(3, 2, false)]
    [InlineData(null, 0, true)]
    public void The_plan_is_ready_when_nobody_is_missing_or_no_number_was_set(int? required, int confirmed, bool ready)
    {
        var summary = HarvestPlanRosterRules.Summary(required, Enumerable.Repeat(HarvestPlanStaffState.Confirmed, confirmed));

        Assert.Equal(ready, summary.Ready);
        Assert.Equal(required is { } r ? Math.Max(0, r - confirmed) : 0, summary.Missing);
    }

    [Theory]
    [InlineData(null, true)]
    [InlineData(1, true)]
    [InlineData(500, true)]
    [InlineData(0, false)]
    [InlineData(501, false)]
    [InlineData(-3, false)]
    public void The_number_needed_is_absent_or_between_1_and_500(int? value, bool valid) => Assert.Equal(valid, HarvestPlanRosterRules.ValidRequiredPeople(value));

    [Theory]
    [InlineData(StatusIds.HarvestPlan.Draft, true)]
    [InlineData(StatusIds.HarvestPlan.Assigned, true)]
    [InlineData(StatusIds.HarvestPlan.Received, true)]
    [InlineData(StatusIds.HarvestPlan.Harvesting, true)]
    [InlineData(StatusIds.HarvestPlan.HandedOver, true)]
    [InlineData(StatusIds.HarvestPlan.Completed, false)]
    [InlineData(StatusIds.HarvestPlan.Cancelled, false)]
    public void The_roster_is_open_until_the_plan_is_completed_or_cancelled(int status, bool open) => Assert.Equal(open, HarvestPlanRosterRules.Open(status));

    [Fact]
    public void A_person_added_outside_the_system_cannot_answer_and_the_others_answer_while_the_plan_is_open()
    {
        Assert.False(HarvestPlanRosterRules.MayRespond(StatusIds.HarvestPlan.Assigned, HarvestPlanStaffState.External));
        Assert.True(HarvestPlanRosterRules.MayRespond(StatusIds.HarvestPlan.Assigned, HarvestPlanStaffState.Invited));
        Assert.True(HarvestPlanRosterRules.MayRespond(StatusIds.HarvestPlan.Harvesting, HarvestPlanStaffState.Declined));
        Assert.False(HarvestPlanRosterRules.MayRespond(StatusIds.HarvestPlan.Completed, HarvestPlanStaffState.Invited));
    }

    [Fact]
    public void The_roster_version_changes_with_the_number_needed_and_with_any_row()
    {
        var plan = new HarvestPlan { StatusId = StatusIds.HarvestPlan.Assigned };
        var row = new HarvestPlanStaffMember { RowVersion = [1, 2, 3] };
        plan.Staff.Add(row);
        var first = HarvestPlanRosterRules.VersionOf(plan);

        Assert.Equal(first, HarvestPlanRosterRules.VersionOf(plan));
        plan.RequiredPeople = 4;
        var withNumber = HarvestPlanRosterRules.VersionOf(plan);
        row.State = HarvestPlanStaffState.Confirmed;
        var withState = HarvestPlanRosterRules.VersionOf(plan);
        row.RowVersion = [1, 2, 4];

        Assert.NotEqual(first, withNumber);
        Assert.NotEqual(withNumber, withState);
        Assert.NotEqual(withState, HarvestPlanRosterRules.VersionOf(plan));
    }
}
