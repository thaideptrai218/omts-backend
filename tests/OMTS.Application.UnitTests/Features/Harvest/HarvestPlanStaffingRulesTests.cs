using OMTS.Application.Features.Harvest.Plans;
using OMTS.Domain.Constants;

namespace OMTS.Application.UnitTests.Features.Harvest;

/// <summary>Who chooses the crew of a plan line in which status, and what becomes of the assignments when the plan's day, lines or status change.</summary>
public class HarvestPlanStaffingRulesTests
{
    private static readonly DateOnly Today = new(2026, 11, 18);

    [Theory]
    // Owner: Draft and Assigned (and Harvesting through the zone right it holds everywhere), never once over.
    [InlineData(StatusIds.HarvestPlan.Draft, true, true, StaffingRefusal.None)]
    [InlineData(StatusIds.HarvestPlan.Assigned, true, true, StaffingRefusal.None)]
    [InlineData(StatusIds.HarvestPlan.Received, true, true, StaffingRefusal.None)]
    [InlineData(StatusIds.HarvestPlan.HandedOver, true, true, StaffingRefusal.WrongStatus)]
    [InlineData(StatusIds.HarvestPlan.Harvesting, true, true, StaffingRefusal.None)]
    [InlineData(StatusIds.HarvestPlan.Completed, true, true, StaffingRefusal.WrongStatus)]
    [InlineData(StatusIds.HarvestPlan.Cancelled, true, true, StaffingRefusal.WrongStatus)]
    // Zone Manager with the work assignment right in the zone: from the hand-over on.
    [InlineData(StatusIds.HarvestPlan.Draft, false, true, StaffingRefusal.Forbidden)]
    [InlineData(StatusIds.HarvestPlan.Assigned, false, true, StaffingRefusal.None)]
    [InlineData(StatusIds.HarvestPlan.Received, false, true, StaffingRefusal.None)]
    [InlineData(StatusIds.HarvestPlan.Harvesting, false, true, StaffingRefusal.None)]
    [InlineData(StatusIds.HarvestPlan.Completed, false, true, StaffingRefusal.WrongStatus)]
    [InlineData(StatusIds.HarvestPlan.Cancelled, false, true, StaffingRefusal.WrongStatus)]
    // A caller with no right in the zone.
    [InlineData(StatusIds.HarvestPlan.Assigned, false, false, StaffingRefusal.Forbidden)]
    [InlineData(StatusIds.HarvestPlan.Harvesting, false, false, StaffingRefusal.Forbidden)]
    public void The_crew_is_chosen_by_the_owner_before_the_hand_over_and_by_the_zone_manager_after(int status, bool isOwner, bool mayWorkInZone, StaffingRefusal expected) =>
        Assert.Equal(expected, HarvestPlanStaffingRules.Refusal(status, isOwner, mayWorkInZone));

    [Fact]
    public void The_crew_is_chosen_only_for_today_or_a_later_day()
    {
        Assert.True(HarvestPlanStaffingRules.DayOpen(Today, Today));
        Assert.True(HarvestPlanStaffingRules.DayOpen(Today.AddDays(3), Today));
        Assert.False(HarvestPlanStaffingRules.DayOpen(Today.AddDays(-1), Today));
    }

    [Theory]
    [InlineData(0, false, StaffingFollow.Remove)]
    [InlineData(2, false, StaffingFollow.Remove)]
    [InlineData(0, true, StaffingFollow.Unlink)]
    [InlineData(-1, false, StaffingFollow.Unlink)]
    [InlineData(-1, true, StaffingFollow.Unlink)]
    public void A_line_that_goes_removes_the_assignments_no_one_checked_in_on_and_keeps_the_rest_as_a_record(int daysFromToday, bool checkedIn, StaffingFollow expected) =>
        Assert.Equal(expected, HarvestPlanStaffingRules.WhenLineGoes(Today.AddDays(daysFromToday), Today, checkedIn));

    [Fact]
    public void An_assignment_follows_the_plan_to_its_new_day_unless_the_employee_checked_in()
    {
        Assert.Equal(StaffingFollow.Keep, HarvestPlanStaffingRules.WhenDateChanges(Today, Today, checkedIn: false));
        Assert.Equal(StaffingFollow.Keep, HarvestPlanStaffingRules.WhenDateChanges(Today, Today, checkedIn: true));
        Assert.Equal(StaffingFollow.Move, HarvestPlanStaffingRules.WhenDateChanges(Today, Today.AddDays(1), checkedIn: false));
        Assert.Equal(StaffingFollow.Unlink, HarvestPlanStaffingRules.WhenDateChanges(Today, Today.AddDays(1), checkedIn: true));
    }
}
