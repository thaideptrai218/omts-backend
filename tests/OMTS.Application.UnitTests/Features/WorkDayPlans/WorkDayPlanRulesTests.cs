using OMTS.Application.Features.Harvest.Plans;
using OMTS.Application.Features.WorkDayPlans;
using OMTS.Domain.Entities.Harvest;
using OMTS.Domain.Entities.Labor;
using OMTS.Domain.Enums;

namespace OMTS.Application.UnitTests.Features.WorkDayPlans;

/// <summary>The status of a work-day plan against its people, the answer of an invited employee, the expiry of invitations and the plan of a harvest line.</summary>
public sealed class WorkDayPlanRulesTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 28, 9, 0, 0, TimeSpan.FromHours(7));

    private static WorkDayPlan Plan(int required, WorkDayPlanStatus status = WorkDayPlanStatus.Staffing) => new() { Code = "KNL000001", RequiredPeople = required, Status = status };

    private static WorkDayPlanMember Member(WorkDayPlan plan, WorkDayMemberState state, DateTimeOffset? deadline = null)
    {
        var member = new WorkDayPlanMember { Plan = plan, State = state, Deadline = deadline, Employee = new() { FullName = "Lan" } };
        plan.Members.Add(member);
        return member;
    }

    [Fact]
    public void A_plan_is_ready_when_confirmed_members_and_outsiders_reach_the_people_needed()
    {
        var plan = Plan(5);
        Member(plan, WorkDayMemberState.Accepted);
        Member(plan, WorkDayMemberState.ConfirmedOnBehalf);
        Member(plan, WorkDayMemberState.Invited);
        Member(plan, WorkDayMemberState.Declined);
        plan.OutsiderHires.Add(new PlanOutsiderHire { People = 2 });

        Assert.Equal(4, WorkDayPlanRules.Present(plan));
        Assert.Equal(WorkDayPlanStatus.Staffing, WorkDayPlanRules.Settle(plan, Now).To);
        Assert.Equal(1, WorkDayPlanRules.Missing(plan));

        plan.OutsiderHires.Add(new PlanOutsiderHire { People = 1 });
        var settlement = WorkDayPlanRules.Settle(plan, Now);

        Assert.True(settlement.BecameReady);
        Assert.Equal(WorkDayPlanStatus.Ready, plan.Status);
        Assert.Equal(0, WorkDayPlanRules.Missing(plan));
    }

    [Fact]
    public void A_plan_that_becomes_full_ends_the_invitations_still_waiting()
    {
        var plan = Plan(2);
        Member(plan, WorkDayMemberState.ConfirmedOnBehalf);
        Member(plan, WorkDayMemberState.Accepted);
        var waiting = Member(plan, WorkDayMemberState.Invited);
        var declined = Member(plan, WorkDayMemberState.Declined);

        var settlement = WorkDayPlanRules.Settle(plan, Now);

        Assert.Equal([waiting], settlement.Unsuccessful);
        Assert.Equal(WorkDayMemberState.Unsuccessful, waiting.State);
        Assert.Equal(Now, waiting.RespondedAt);
        Assert.Equal(WorkDayMemberState.Declined, declined.State);
    }

    [Fact]
    public void Raising_the_number_or_a_removal_makes_a_ready_plan_short_again_but_not_one_that_is_running()
    {
        var plan = Plan(1);
        var member = Member(plan, WorkDayMemberState.ConfirmedOnBehalf);
        WorkDayPlanRules.Settle(plan, Now);
        Assert.Equal(WorkDayPlanStatus.Ready, plan.Status);

        plan.RequiredPeople = 3;
        Assert.True(WorkDayPlanRules.Settle(plan, Now).BecameShort);
        Assert.Equal(WorkDayPlanStatus.Staffing, plan.Status);

        plan.RequiredPeople = 1;
        WorkDayPlanRules.Settle(plan, Now);
        plan.Members.Remove(member);
        Assert.Equal(WorkDayPlanStatus.Staffing, WorkDayPlanRules.Settle(plan, Now).To);

        var running = Plan(2, WorkDayPlanStatus.InProgress);
        Member(running, WorkDayMemberState.ConfirmedOnBehalf);
        Assert.Equal(WorkDayPlanStatus.InProgress, WorkDayPlanRules.Settle(running, Now).To);
    }

    [Theory]
    [InlineData(WorkDayPlanStatus.Completed)]
    [InlineData(WorkDayPlanStatus.Cancelled)]
    public void A_plan_that_is_over_is_not_settled(WorkDayPlanStatus status)
    {
        var plan = Plan(1, status);
        Member(plan, WorkDayMemberState.Accepted);

        Assert.Equal(status, WorkDayPlanRules.Settle(plan, Now).To);
        Assert.Equal(status, plan.Status);
    }

    [Fact]
    public void An_answer_in_time_is_taken_and_a_late_one_is_unsuccessful()
    {
        var plan = Plan(3);
        var inTime = Member(plan, WorkDayMemberState.Invited, Now.AddMinutes(10));
        var declines = Member(plan, WorkDayMemberState.Invited, Now.AddMinutes(10));
        var late = Member(plan, WorkDayMemberState.Invited, Now.AddMinutes(-1));

        Assert.Equal(WorkDayAnswer.Taken, WorkDayPlanRules.Answer(plan, inTime, true, null, Now));
        Assert.Equal(WorkDayMemberState.Accepted, inTime.State);
        Assert.Equal(WorkDayAnswer.Taken, WorkDayPlanRules.Answer(plan, declines, false, "Bận", Now));
        Assert.Equal((WorkDayMemberState.Declined, "Bận"), (declines.State, declines.Reason));
        Assert.Equal(WorkDayAnswer.TooLate, WorkDayPlanRules.Answer(plan, late, true, null, Now));
        Assert.Equal(WorkDayMemberState.Unsuccessful, late.State);
        Assert.Equal(WorkDayAnswer.NotPending, WorkDayPlanRules.Answer(plan, late, true, null, Now));
    }

    [Fact]
    public void An_accept_after_the_plan_became_full_is_unsuccessful()
    {
        var plan = Plan(1, WorkDayPlanStatus.Ready);
        Member(plan, WorkDayMemberState.ConfirmedOnBehalf);
        var accepting = Member(plan, WorkDayMemberState.Invited, Now.AddMinutes(10));

        Assert.Equal(WorkDayAnswer.TooLate, WorkDayPlanRules.Answer(plan, accepting, true, null, Now));
        Assert.Equal(WorkDayMemberState.Unsuccessful, accepting.State);
        Assert.Equal(1, WorkDayPlanRules.Present(plan));
    }

    [Fact]
    public void Invitations_past_their_deadline_expire_and_the_others_wait()
    {
        var plan = Plan(2);
        var overdue = Member(plan, WorkDayMemberState.Invited, Now.AddMinutes(-1));
        var waiting = Member(plan, WorkDayMemberState.Invited, Now.AddMinutes(5));
        var confirmed = Member(plan, WorkDayMemberState.Accepted, Now.AddMinutes(-30));

        Assert.Equal([overdue], WorkDayPlanRules.ExpireOverdue(plan, Now));
        Assert.Equal(WorkDayMemberState.Expired, overdue.State);
        Assert.Equal(WorkDayMemberState.Invited, waiting.State);
        Assert.Equal(WorkDayMemberState.Accepted, confirmed.State);
        Assert.Empty(WorkDayPlanRules.ExpireOverdue(plan, Now));
    }

    [Fact]
    public void The_plan_of_a_harvest_line_is_completed_with_the_line_and_open_again_when_the_line_is()
    {
        var plan = Plan(1, WorkDayPlanStatus.Ready);
        Member(plan, WorkDayMemberState.Accepted);

        Assert.False(WorkDayPlanRules.FollowHarvestLine(plan, lineDone: false, Now));
        Assert.True(WorkDayPlanRules.FollowHarvestLine(plan, lineDone: true, Now));
        Assert.Equal((WorkDayPlanStatus.Completed, Now), (plan.Status, plan.CompletedAt));

        Assert.True(WorkDayPlanRules.FollowHarvestLine(plan, lineDone: false, Now));
        Assert.Equal(WorkDayPlanStatus.Ready, plan.Status);
        Assert.Null(plan.CompletedAt);
    }

    [Fact]
    public void A_harvest_plan_cannot_be_handed_out_while_a_plan_of_its_lines_is_short_and_the_answer_lists_them()
    {
        var raftA = new OMTS.Domain.Entities.Farming.Raft { Code = "B01" };
        var raftB = new OMTS.Domain.Entities.Farming.Raft { Code = "B02" };
        var staffed = Plan(1, WorkDayPlanStatus.Ready);
        var short_ = Plan(4);
        Member(short_, WorkDayMemberState.Accepted);
        var harvest = new HarvestPlan
        {
            Code = "KHT000007",
            Lines = [new HarvestPlanLine { LineNumber = 1, Raft = raftA, WorkDayPlan = staffed }, new HarvestPlanLine { LineNumber = 2, Raft = raftB, WorkDayPlan = short_ }],
        };

        var refused = WorkDayPlanHarvestLink.GateAssign(harvest);

        Assert.NotNull(refused);
        Assert.Equal(OMTS.Application.Common.Results.ErrorKind.Conflict, refused!.Kind);
        Assert.Contains("B02", refused.Message.Text);
        Assert.DoesNotContain("B01", refused.Message.Text);
        var listed = Assert.IsType<List<ShortWorkDayPlan>>(refused.Details!["plans"]);
        Assert.Equal([new ShortWorkDayPlan(short_.Id, "KNL000001", "B02", 4, 1)], listed);

        short_.RequiredPeople = 1;
        WorkDayPlanRules.Settle(short_, Now);
        Assert.Null(WorkDayPlanHarvestLink.GateAssign(harvest));
    }

    [Theory]
    [InlineData("Thu hoạch", WorkDayPlanKind.Harvest)]
    [InlineData("Ươm", WorkDayPlanKind.Nursery)]
    [InlineData("treo giống", WorkDayPlanKind.Seeding)]
    public void The_kind_decides_the_job_type_and_the_job_type_the_kind(string job, WorkDayPlanKind kind)
    {
        Assert.Equal(kind, WorkDayPlanRules.KindOfJob(job));
        Assert.Equal(job, WorkDayPlanRules.JobNameOf(kind), ignoreCase: true);
    }

    [Fact]
    public void A_job_type_that_is_not_field_work_has_no_kind()
    {
        Assert.Null(WorkDayPlanRules.KindOfJob("Bón phân"));
        Assert.Null(WorkDayPlanRules.KindOfJob(null));
    }

    [Theory]
    [InlineData(0, false)]
    [InlineData(1, true)]
    [InlineData(500, true)]
    [InlineData(501, false)]
    public void The_people_needed_are_one_to_five_hundred(int people, bool valid) => Assert.Equal(valid, WorkDayPlanRules.ValidRequiredPeople(people));

    [Fact]
    public void The_people_needed_are_required()
    {
        Assert.False(WorkDayPlanRules.ValidRequiredPeople(null));
    }
}
