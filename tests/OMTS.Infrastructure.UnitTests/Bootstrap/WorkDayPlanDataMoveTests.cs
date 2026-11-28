using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using OMTS.Application.Common.Interfaces.Persistence;
using OMTS.Application.Features.WorkDayPlans;
using OMTS.Domain.Constants;
using OMTS.Domain.Entities.Farming;
using OMTS.Domain.Entities.Harvest;
using OMTS.Domain.Entities.Identity;
using OMTS.Domain.Entities.Labor;
using OMTS.Domain.Enums;
using OMTS.Infrastructure.Bootstrap;
using OMTS.Infrastructure.Persistence;

namespace OMTS.Infrastructure.UnitTests.Bootstrap;

/// <summary>The open harvest plans get the work-day plans of their lines, with the roster and the crew they already have.</summary>
public sealed class WorkDayPlanDataMoveTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 28, 9, 0, 0, TimeSpan.FromHours(7));

    private sealed class FixedClock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    private sealed class Numbers : IDocumentNumberAllocator
    {
        private long _last;

        public Task<long> NextAsync(string key, CancellationToken cancellationToken) => Task.FromResult(++_last);
    }

    private sealed class Responsibles(Guid? answer) : IHarvestResponsibleLookup
    {
        public Task<Guid?> ResponsibleForHarvestAsync(Guid zoneId, HarvestPlanExecutor executor, Guid? callerId, CancellationToken cancellationToken) => Task.FromResult(answer);
    }

    private sealed class Data
    {
        public Guid OwnerId { get; } = Guid.NewGuid();
        public FarmingZone Zone { get; } = new() { Code = "KV01", Name = "Khu 1" };
        public Raft RaftA { get; }
        public Raft RaftB { get; }
        public Employee Lan { get; } = new() { Code = "NV001", FullName = "Lan" };
        public Employee Minh { get; } = new() { Code = "NV002", FullName = "Minh" };
        public Employee Nam { get; } = new() { Code = "NV003", FullName = "Nam" };
        public Employee Seasonal { get; } = new() { Code = "NV004", FullName = "Bình" };
        public HarvestPlan Harvest { get; }
        public HarvestPlanLine First { get; }
        public HarvestPlanLine Second { get; }

        public Data(int status = StatusIds.HarvestPlan.Assigned)
        {
            RaftA = new Raft { Code = "B01", ZoneId = Zone.Id, Zone = Zone };
            RaftB = new Raft { Code = "B02", ZoneId = Zone.Id, Zone = Zone };
            First = new HarvestPlanLine { LineNumber = 1, RaftId = RaftA.Id, Raft = RaftA };
            Second = new HarvestPlanLine { LineNumber = 2, RaftId = RaftB.Id, Raft = RaftB };
            Harvest = new HarvestPlan
            {
                Code = "KHT000001",
                StatusId = status,
                PlannedAt = Now.AddDays(1),
                PlannedDate = new DateOnly(2026, 11, 29),
                CreatedByAccountId = OwnerId,
                RequiredPeople = 4,
                Lines = [First, Second],
                Staff =
                [
                    Row(Lan, HarvestPlanStaffState.Confirmed),
                    Row(Minh, HarvestPlanStaffState.Invited),
                    Row(Seasonal, HarvestPlanStaffState.External),
                ],
            };
        }

        private HarvestPlanStaffMember Row(Employee employee, HarvestPlanStaffState state) => new()
        {
            Employee = employee, EmployeeId = employee.Id, State = state, InvitedAt = Now.AddHours(-3), RespondedAt = state == HarvestPlanStaffState.Invited ? null : Now.AddHours(-2), AddedByAccountId = OwnerId,
        };
    }

    private static OmtsDbContext NewDb(string name) => new(new DbContextOptionsBuilder<OmtsDbContext>().UseInMemoryDatabase(name).Options);

    private static WorkDayPlanDataMove Step(OmtsDbContext db, Guid? responsible = null) =>
        new(db, new Responsibles(responsible), new Numbers(), new FixedClock(Now), NullLogger<WorkDayPlanDataMove>.Instance);

    [Fact]
    public async Task Each_line_of_an_open_harvest_plan_gets_a_plan_and_the_roster_goes_to_the_first_one()
    {
        var name = $"move-{Guid.NewGuid()}";
        var data = new Data();
        await using (var seed = NewDb(name))
        {
            seed.AddRange(data.Zone, data.RaftA, data.RaftB, data.Lan, data.Minh, data.Nam, data.Seasonal);
            seed.HarvestPlans.Add(data.Harvest);
            await seed.SaveChangesAsync();
        }

        var manager = Guid.NewGuid();
        await using (var db = NewDb(name))
        {
            var message = await Step(db, manager).RunAsync(default);
            Assert.Contains("2 plan(s) made", message);
        }

        await using var read = NewDb(name);
        var plans = await read.WorkDayPlans.Include(p => p.Members).OrderBy(p => p.Code).ToListAsync();
        Assert.Equal(["KNL000001", "KNL000002"], plans.Select(p => p.Code));
        Assert.All(plans, p => Assert.Equal((WorkDayPlanKind.Harvest, new DateOnly(2026, 11, 29), data.Zone.Id, manager), (p.Kind, p.WorkDate, p.ZoneId, p.ResponsibleAccountId)));
        var first = plans[0];
        Assert.Equal((data.First.Id, data.RaftA.Id, 4), (first.HarvestPlanLineId, first.RaftId, first.RequiredPeople));
        Assert.Equal(WorkDayPlanStatus.Staffing, first.Status);
        var byPerson = first.Members.ToDictionary(m => m.EmployeeId);
        Assert.Equal(WorkDayMemberState.Accepted, byPerson[data.Lan.Id].State);
        Assert.Equal(WorkDayMemberState.Invited, byPerson[data.Minh.Id].State);
        Assert.NotNull(byPerson[data.Minh.Id].Deadline);
        var hired = byPerson[data.Seasonal.Id];
        Assert.Equal((WorkDayMemberState.ConfirmedOnBehalf, "chuyển dữ liệu", data.OwnerId), (hired.State, hired.Reason, hired.ConfirmedByAccountId));
        var second = plans[1];
        Assert.Equal((data.Second.Id, 1, WorkDayPlanStatus.Staffing), (second.HarvestPlanLineId, second.RequiredPeople, second.Status));
        Assert.Empty(second.Members);
    }

    [Fact]
    public async Task The_assignments_of_a_line_are_linked_to_its_plan_and_their_people_become_members()
    {
        var name = $"move-{Guid.NewGuid()}";
        var data = new Data();
        var rostered = new WorkAssignment { EmployeeId = data.Lan.Id, Date = data.Harvest.PlannedDate, RaftId = data.RaftA.Id, ZoneId = data.Zone.Id, HarvestPlanLineId = data.First.Id, AssignedByAccountId = data.OwnerId };
        var unlisted = new WorkAssignment { EmployeeId = data.Nam.Id, Date = data.Harvest.PlannedDate, RaftId = data.RaftB.Id, ZoneId = data.Zone.Id, HarvestPlanLineId = data.Second.Id, AssignedByAccountId = data.OwnerId };
        var invitedStillWaiting = new WorkAssignment { EmployeeId = data.Minh.Id, Date = data.Harvest.PlannedDate, RaftId = data.RaftA.Id, ZoneId = data.Zone.Id, HarvestPlanLineId = data.First.Id, AssignedByAccountId = data.OwnerId };
        await using (var seed = NewDb(name))
        {
            seed.AddRange(data.Zone, data.RaftA, data.RaftB, data.Lan, data.Minh, data.Nam, data.Seasonal, rostered, unlisted, invitedStillWaiting);
            seed.HarvestPlans.Add(data.Harvest);
            await seed.SaveChangesAsync();
        }

        await using (var db = NewDb(name))
        {
            Assert.Contains("3 assignment(s) linked", await Step(db).RunAsync(default));
        }

        await using var read = NewDb(name);
        var plans = await read.WorkDayPlans.Include(p => p.Members).OrderBy(p => p.Code).ToListAsync();
        var assignments = await read.WorkAssignments.ToDictionaryAsync(a => a.EmployeeId);
        Assert.Equal(plans[0].Id, assignments[data.Lan.Id].WorkDayPlanId);
        Assert.Equal(plans[0].Id, assignments[data.Minh.Id].WorkDayPlanId);
        Assert.Equal(plans[1].Id, assignments[data.Nam.Id].WorkDayPlanId);
        var lan = plans[0].Members.Single(m => m.EmployeeId == data.Lan.Id);
        Assert.Equal((WorkDayMemberState.Accepted, assignments[data.Lan.Id].Id), (lan.State, lan.WorkAssignmentId));
        var minh = plans[0].Members.Single(m => m.EmployeeId == data.Minh.Id);
        Assert.Equal((WorkDayMemberState.ConfirmedOnBehalf, "chuyển dữ liệu"), (minh.State, minh.Reason));
        var nam = plans[1].Members.Single();
        Assert.Equal((data.Nam.Id, WorkDayMemberState.ConfirmedOnBehalf, assignments[data.Nam.Id].Id), (nam.EmployeeId, nam.State, nam.WorkAssignmentId));
        // The second plan needs the one person it has; the first has Lan, Minh and the hired worker against four.
        Assert.Equal((1, WorkDayPlanStatus.Ready), (plans[1].RequiredPeople, plans[1].Status));
        Assert.Equal(WorkDayPlanStatus.Staffing, plans[0].Status);
    }

    [Fact]
    public async Task A_line_that_is_done_has_its_plan_completed()
    {
        var name = $"move-{Guid.NewGuid()}";
        var data = new Data();
        data.Second.Lot = new HarvestLot { LotCode = "L1" };
        await using (var seed = NewDb(name))
        {
            seed.AddRange(data.Zone, data.RaftA, data.RaftB, data.Lan, data.Minh, data.Nam, data.Seasonal);
            seed.HarvestPlans.Add(data.Harvest);
            await seed.SaveChangesAsync();
        }

        await using (var db = NewDb(name))
        {
            await Step(db).RunAsync(default);
        }

        await using var read = NewDb(name);
        var plans = await read.WorkDayPlans.OrderBy(p => p.Code).ToListAsync();
        Assert.Equal((WorkDayPlanStatus.Staffing, WorkDayPlanStatus.Completed), (plans[0].Status, plans[1].Status));
    }

    [Theory]
    [InlineData(StatusIds.HarvestPlan.Completed)]
    [InlineData(StatusIds.HarvestPlan.Cancelled)]
    public async Task A_harvest_plan_that_is_over_is_left_alone(int status)
    {
        var name = $"move-{Guid.NewGuid()}";
        var data = new Data(status);
        await using (var seed = NewDb(name))
        {
            seed.AddRange(data.Zone, data.RaftA, data.RaftB, data.Lan, data.Minh, data.Nam, data.Seasonal);
            seed.HarvestPlans.Add(data.Harvest);
            await seed.SaveChangesAsync();
        }

        await using var db = NewDb(name);
        Assert.Contains("nothing to move", await Step(db).RunAsync(default));
        Assert.Empty(await db.WorkDayPlans.ToListAsync());
    }

    [Fact]
    public async Task Running_the_step_again_makes_nothing_more()
    {
        var name = $"move-{Guid.NewGuid()}";
        var data = new Data();
        await using (var seed = NewDb(name))
        {
            seed.AddRange(data.Zone, data.RaftA, data.RaftB, data.Lan, data.Minh, data.Nam, data.Seasonal);
            seed.HarvestPlans.Add(data.Harvest);
            await seed.SaveChangesAsync();
        }

        await using (var first = NewDb(name))
        {
            await Step(first).RunAsync(default);
        }

        await using var second = NewDb(name);
        Assert.Contains("nothing to move", await Step(second).RunAsync(default));
        Assert.Equal(2, await second.WorkDayPlans.CountAsync());
        Assert.Equal(3, await second.WorkDayPlanMembers.CountAsync());
    }
}
