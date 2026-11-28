using Microsoft.Extensions.Caching.Memory;
using OMTS.Application.Common.Concurrency;
using OMTS.Application.Common.Interfaces;
using OMTS.Application.Common.Interfaces.Persistence;
using OMTS.Application.Common.Messages;
using OMTS.Application.Common.Results;
using OMTS.Application.Features.Audit;
using OMTS.Application.Features.Authorization;
using OMTS.Application.Features.Authorization.Models;
using OMTS.Application.Features.Harvest;
using OMTS.Application.Features.Harvest.Plans;
using OMTS.Application.Features.Labor;
using OMTS.Application.Features.Labor.Work;
using OMTS.Application.UnitTests.Fakes;
using OMTS.Application.UnitTests.Features.Purchasing;
using OMTS.Domain.Constants;
using OMTS.Domain.Entities.Farming;
using OMTS.Domain.Entities.Harvest;
using OMTS.Domain.Entities.Identity;
using OMTS.Domain.Entities.Labor;
using OMTS.Domain.Enums;

namespace OMTS.Application.UnitTests.Features.Harvest;

/// <summary>The Nhân công of a harvest plan: who may choose the crew, the assignments it creates and removes, and how they follow the plan when its day or lines change or it is cancelled.</summary>
public sealed class HarvestPlanStaffingServiceTests
{
    // Business day 18/11/2026 at 09:00 (UTC+7).
    private static readonly DateTimeOffset Now = new(2026, 11, 18, 9, 0, 0, TimeSpan.FromHours(7));
    private static readonly DateOnly Today = new(2026, 11, 18);
    private static readonly DateOnly Tomorrow = Today.AddDays(1);

    private sealed class Catalog : IStatusCatalog
    {
        public string CodeOf(int statusId) => $"S{statusId}";

        public string DisplayNameOf(int statusId) => $"Status {statusId}";
    }

    /// <summary>The work assignments and attendance kept in memory; an assignment added gets its navigations as the database would give them.</summary>
    internal sealed class Store(IReadOnlyList<Employee> employees, IReadOnlyList<Raft> rafts, JobType job) : IWorkAssignmentRepository, IAttendanceRepository
    {
        public List<WorkAssignment> Rows { get; } = [];
        public List<Attendance> Attendance { get; } = [];

        public Task<WorkAssignment?> GetAsync(Guid id, CancellationToken cancellationToken) => Task.FromResult(Rows.FirstOrDefault(w => w.Id == id));

        public Task<IReadOnlyList<WorkAssignment>> ListDayAsync(DateOnly day, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<WorkAssignment>>(Rows.Where(w => w.Date == day).ToList());

        public Task<IReadOnlyList<WorkAssignment>> ListOfEmployeesAsync(IReadOnlyCollection<Guid> employeeIds, DateOnly day, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<WorkAssignment>>(Rows.Where(w => w.Date == day && employeeIds.Contains(w.EmployeeId)).ToList());

        public Task<IReadOnlyList<WorkAssignment>> ListOfEmployeeAsync(Guid employeeId, DateOnly from, DateOnly to, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<IReadOnlyList<JobType>> GetJobTypesAsync(IReadOnlyCollection<Guid> ids, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<JobType>>(ids.Contains(job.Id) ? [job] : []);

        public Task<IReadOnlyList<WorkAssignment>> ListOfPlanLinesAsync(IReadOnlyCollection<Guid> lineIds, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<WorkAssignment>>(Rows.Where(w => w.HarvestPlanLineId is { } l && lineIds.Contains(l)).OrderBy(w => w.Employee.FullName).ToList());

        public Task<IReadOnlyList<WorkAssignment>> ListOfPlansAsync(IReadOnlyCollection<Guid> planIds, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<WorkAssignment>>(Rows.Where(w => w.WorkDayPlanId is { } p && planIds.Contains(p)).ToList());

        public Task<WorkAssignment?> FindFirstAsync(Guid employeeId, DateOnly day, CancellationToken cancellationToken) =>
            Task.FromResult(Rows.FirstOrDefault(w => w.EmployeeId == employeeId && w.Date == day && w.StartsAt is null));

        public void Add(WorkAssignment assignment)
        {
            assignment.Employee = employees.First(e => e.Id == assignment.EmployeeId);
            assignment.JobType = job;
            if (assignment.RaftId is { } raftId)
            {
                assignment.Raft = rafts.First(r => r.Id == raftId);
                assignment.Zone = assignment.Raft.Zone;
            }

            Rows.Add(assignment);
        }

        public void Remove(WorkAssignment assignment) => Rows.Remove(assignment);

        public Task<Attendance?> GetAsync(Guid employeeId, DateOnly day, CancellationToken cancellationToken) =>
            Task.FromResult(Attendance.FirstOrDefault(a => a.EmployeeId == employeeId && a.Date == day));

        Task<IReadOnlyList<Attendance>> IAttendanceRepository.ListOfEmployeesAsync(IReadOnlyCollection<Guid> employeeIds, DateOnly day, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<Attendance>>(Attendance.Where(a => a.Date == day && employeeIds.Contains(a.EmployeeId)).ToList());

        Task<IReadOnlyList<Attendance>> IAttendanceRepository.ListDayAsync(DateOnly day, CancellationToken cancellationToken) => throw new NotSupportedException();

        Task<IReadOnlyList<Attendance>> IAttendanceRepository.ListOfEmployeeAsync(Guid employeeId, DateOnly from, DateOnly to, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public void Add(Attendance attendance) => Attendance.Add(attendance);
    }

    private sealed class Fixture
    {
        public FarmingZone ZoneA { get; } = new() { Code = "KV01" };
        public FarmingZone ZoneB { get; } = new() { Code = "KV02" };
        public Raft RaftA { get; }
        public Raft RaftA2 { get; }
        public Raft RaftB { get; }
        public Employee Lan { get; } = new() { Code = "NV001", FullName = "Lan" };
        public Employee Minh { get; } = new() { Code = "NV002", FullName = "Minh" };
        public Employee Nam { get; } = new() { Code = "NV003", FullName = "Nam" };
        public JobType Job { get; } = new() { Name = RecordCrewJobsName, StatusId = StatusIds.Usage.InUse };
        public HarvestPlan Plan { get; }
        public HarvestPlanLine LineA { get; }
        public HarvestPlanLine LineB { get; }
        public Store Store { get; }
        public FakeAuditEventRepository Audit { get; } = new();
        public int Saves { get; private set; }
        public JobType? JobInCatalogue { get; set; }

        public Fixture(int status = StatusIds.HarvestPlan.Assigned, DateOnly? day = null)
        {
            RaftA = new Raft { Code = "B01", ZoneId = ZoneA.Id, Zone = ZoneA };
            RaftA2 = new Raft { Code = "B02", ZoneId = ZoneA.Id, Zone = ZoneA };
            RaftB = new Raft { Code = "B11", ZoneId = ZoneB.Id, Zone = ZoneB };
            LineA = Line(1, RaftA);
            LineB = Line(2, RaftB);
            Plan = new HarvestPlan
            {
                Code = "KHT000001",
                StatusId = status,
                PlannedDate = day ?? Today,
                PlannedAt = new DateTimeOffset((day ?? Today).ToDateTime(new TimeOnly(8, 0)), TimeSpan.FromHours(7)),
                AssignedAt = status == StatusIds.HarvestPlan.Draft ? null : Now,
                Lines = [LineA, LineB],
            };
            Store = new Store([Lan, Minh, Nam], [RaftA, RaftA2, RaftB], Job);
            JobInCatalogue = Job;
        }

        private static HarvestPlanLine Line(int number, Raft raft) => new()
        {
            LineNumber = number,
            RaftId = raft.Id,
            Raft = raft,
            Lanes = [new HarvestPlanLane { Lane = new Lane { LaneNumber = number } }, new HarvestPlanLane { Lane = new Lane { LaneNumber = number + 1 } }],
        };

        public WorkAssignment Assign(Employee employee, HarvestPlanLine? line, DateOnly? date = null, Raft? raft = null)
        {
            var r = raft ?? line?.Raft ?? RaftA;
            var row = new WorkAssignment { EmployeeId = employee.Id, Employee = employee, Date = date ?? Plan.PlannedDate, RaftId = r.Id, Raft = r, ZoneId = r.ZoneId, Zone = r.Zone, JobTypeId = Job.Id, JobType = Job, HarvestPlanLineId = line?.Id };
            Store.Rows.Add(row);
            return row;
        }

        public void CheckIn(Employee employee, DateOnly day) => Store.Attendance.Add(new Attendance { EmployeeId = employee.Id, Date = day, CheckInAt = Now });

        public IUnitOfWork Uow() => StubProxy.Create<IUnitOfWork>(h =>
        {
            h["SaveChangesAsync"] = _ => ++Saves;
            h["MarkChanged"] = _ => null;
            h["DiscardChanges"] = _ => null;
        });
    }

    private const string RecordCrewJobsName = "Thu hoạch";

    private static (ZoneAccessGuard Guard, Guid AccountId) Access(AccountAccess access)
    {
        var cache = new PermissionCache(new MemoryCache(new MemoryCacheOptions()));
        cache.Set(cache.KeyFor(access.AccountId), access);
        var permissions = new PermissionService(StubProxy.Create<IAccountRepository>(_ => { }), StubProxy.Create<IAccessGrantRepository>(_ => { }), StubProxy.Create<IFarmingZoneRepository>(_ => { }), cache);
        return (new ZoneAccessGuard(new FakeCurrentUser(access.AccountId), permissions), access.AccountId);
    }

    private static AccountAccess Owner() => new(Guid.NewGuid(), SubjectType.CooperativeOwner, false, true, []);

    private static AccountAccess Manager(params Guid[] zones) => new(
        Guid.NewGuid(), SubjectType.Staff, false, true,
        zones.Select(z => new ZoneGrant(z, Guid.NewGuid(), FixedRoles.ZoneManager.Code, FixedRoles.ZoneManager.Name, FixedRoles.ZoneManager.GrantedPermissions)).ToList());

    private static HarvestPlanStaffingService Service(Fixture f, AccountAccess caller)
    {
        var (guard, accountId) = Access(caller);
        var user = new FakeCurrentUser(accountId);
        var uow = f.Uow();
        var clock = new FixedTimeProvider(Now);
        var catalog = new Catalog();
        var audit = new AuditLogService(f.Audit, user, catalog, clock);
        var payPeriods = new PayPeriodLock(StubProxy.Create<IPayrollRepository>(h => h["ListEndingOnOrAfterAsync"] = _ => (IReadOnlyList<Payroll>)new List<Payroll>()), catalog, uow);
        var employees = new[] { f.Lan, f.Minh, f.Nam };
        var work = new WorkAssignmentService(
            f.Store,
            f.Store,
            StubProxy.Create<IWorkEmployeeReader>(h => h["GetForWorkAsync"] = a => (IReadOnlyList<Employee>)employees.Where(e => ((IReadOnlyCollection<Guid>)a[0]!).Contains(e.Id)).ToList()),
            StubProxy.Create<IEmployeeRepository>(h => h["AddWorkingZone"] = _ => null),
            StubProxy.Create<IEmployeeRequestRepository>(_ => { }),
            StubProxy.Create<IRaftRepository>(h => h["GetByIdsAsync"] = a => (IReadOnlyList<Raft>)new[] { f.RaftA, f.RaftA2, f.RaftB }.Where(r => ((IReadOnlyCollection<Guid>)a[0]!).Contains(r.Id)).ToList()),
            StubProxy.Create<IFarmingZoneRepository>(_ => { }),
            guard, payPeriods, catalog, user, audit, uow, clock);
        return new HarvestPlanStaffingService(
            StubProxy.Create<IHarvestPlanRepository>(h => h["GetForEditAsync"] = a => (Guid)a[0]! == f.Plan.Id ? f.Plan : null),
            f.Store,
            f.Store,
            StubProxy.Create<IJobTypeRepository>(h => h["FindByNameAsync"] = _ => f.JobInCatalogue),
            work,
            new HarvestPlanAccess(guard),
            guard, payPeriods, catalog,
            new RowVersionGuard(StubProxy.Create<IAccountRepository>(_ => { }), uow),
            audit, uow, clock);
    }

    private static SaveHarvestPlanLineStaffingRequest Crew(params Employee[] employees) => new(employees.Select(e => e.Id).ToList());

    // ---- Choosing the crew ----

    [Fact]
    public async Task The_owner_gives_each_employee_a_harvest_assignment_on_the_raft_of_the_line_for_the_day_of_the_plan()
    {
        var f = new Fixture(StatusIds.HarvestPlan.Draft, Tomorrow);

        var result = await Service(f, Owner()).SaveLineAsync(f.Plan.Id, f.LineA.Id, Crew(f.Lan, f.Minh), default);

        Assert.True(result.IsSuccess);
        Assert.Equal(2, f.Store.Rows.Count);
        Assert.All(f.Store.Rows, a =>
        {
            Assert.Equal(f.LineA.Id, a.HarvestPlanLineId);
            Assert.Equal(f.RaftA.Id, a.RaftId);
            Assert.Equal(f.ZoneA.Id, a.ZoneId);
            Assert.Equal(f.Job.Id, a.JobTypeId);
            Assert.Equal(Tomorrow, a.Date);
            Assert.Null(a.StartsAt);
        });
        Assert.Equal(["Lan", "Minh"], result.Value!.Employees.Select(e => e.Name));
        Assert.All(result.Value.Employees, e => Assert.False(e.CheckedIn));
        Assert.Equal(Tomorrow, result.Value.PlannedDate);
        Assert.Equal(1, f.Saves);
        Assert.Equal(2, f.Audit.Added.Count(e => e.Action == "WorkAssignment.Created"));
        Assert.Single(f.Audit.Added, e => e.Action == "HarvestPlan.StaffingChanged");
    }

    [Fact]
    public async Task Saving_the_same_crew_again_changes_nothing()
    {
        var f = new Fixture();
        f.Assign(f.Lan, f.LineA);

        var result = await Service(f, Owner()).SaveLineAsync(f.Plan.Id, f.LineA.Id, Crew(f.Lan), default);

        Assert.True(result.IsSuccess);
        Assert.Single(f.Store.Rows);
        Assert.Equal(0, f.Saves);
        Assert.Empty(f.Audit.Added);
    }

    [Fact]
    public async Task An_employee_left_out_of_the_crew_loses_the_assignment_and_the_others_keep_theirs()
    {
        var f = new Fixture();
        var kept = f.Assign(f.Lan, f.LineA);
        f.Assign(f.Minh, f.LineA);

        var result = await Service(f, Owner()).SaveLineAsync(f.Plan.Id, f.LineA.Id, Crew(f.Lan, f.Nam), default);

        Assert.True(result.IsSuccess);
        Assert.Equal(["Lan", "Nam"], f.Store.Rows.Select(a => a.Employee.FullName).Order());
        Assert.Contains(kept, f.Store.Rows);
        Assert.Single(f.Audit.Added, e => e.Action == "WorkAssignment.Removed");
        Assert.Single(f.Audit.Added, e => e.Action == "WorkAssignment.Created");
    }

    [Fact]
    public async Task An_employee_who_checked_in_that_day_cannot_be_taken_off_and_nothing_changes()
    {
        var f = new Fixture();
        f.Assign(f.Lan, f.LineA);
        f.Assign(f.Minh, f.LineA);
        f.CheckIn(f.Minh, Today);

        var result = await Service(f, Owner()).SaveLineAsync(f.Plan.Id, f.LineA.Id, Crew(f.Lan, f.Nam), default);

        Assert.False(result.IsSuccess);
        Assert.Equal(ErrorKind.Conflict, result.Error!.Kind);
        Assert.Equal(MessageCodes.Labor07, result.Error.Message.Code);
        Assert.Equal(["Lan", "Minh"], f.Store.Rows.Select(a => a.Employee.FullName).Order());
        Assert.Equal(0, f.Saves);
    }

    [Fact]
    public async Task A_checked_in_employee_stays_on_the_line_when_the_crew_still_names_them()
    {
        var f = new Fixture();
        f.Assign(f.Minh, f.LineA);
        f.CheckIn(f.Minh, Today);

        var result = await Service(f, Owner()).SaveLineAsync(f.Plan.Id, f.LineA.Id, Crew(f.Minh, f.Lan), default);

        Assert.True(result.IsSuccess);
        Assert.Equal(["Lan", "Minh"], result.Value!.Employees.Select(e => e.Name));
        Assert.True(result.Value.Employees.Single(e => e.Name == "Minh").CheckedIn);
        Assert.False(result.Value.Employees.Single(e => e.Name == "Lan").CheckedIn);
    }

    [Fact]
    public async Task An_employee_already_on_another_raft_that_day_is_a_conflict_until_the_move_is_confirmed()
    {
        var f = new Fixture();
        var elsewhere = f.Assign(f.Lan, null, raft: f.RaftA2);

        var refused = await Service(f, Owner()).SaveLineAsync(f.Plan.Id, f.LineA.Id, Crew(f.Lan), default);

        Assert.Equal(MessageCodes.Labor06, refused.Error!.Message.Code);
        Assert.Equal(f.RaftA2.Id, elsewhere.RaftId);
        Assert.Null(elsewhere.HarvestPlanLineId);

        var moved = await Service(f, Owner()).SaveLineAsync(f.Plan.Id, f.LineA.Id, new SaveHarvestPlanLineStaffingRequest([f.Lan.Id], MoveExisting: true), default);

        Assert.True(moved.IsSuccess);
        Assert.Equal(f.RaftA.Id, elsewhere.RaftId);
        Assert.Equal(f.LineA.Id, elsewhere.HarvestPlanLineId);
        Assert.Single(f.Store.Rows);
    }

    [Fact]
    public async Task An_employee_with_work_on_the_same_raft_but_not_for_the_plan_is_linked_to_the_line()
    {
        var f = new Fixture();
        var existing = f.Assign(f.Lan, null, raft: f.RaftA);

        var result = await Service(f, Owner()).SaveLineAsync(f.Plan.Id, f.LineA.Id, Crew(f.Lan), default);

        Assert.True(result.IsSuccess);
        Assert.Equal(f.LineA.Id, existing.HarvestPlanLineId);
        Assert.Single(f.Store.Rows);
    }

    [Fact]
    public async Task More_than_a_hundred_employees_or_a_repeated_one_is_invalid()
    {
        var f = new Fixture();

        var twice = await Service(f, Owner()).SaveLineAsync(f.Plan.Id, f.LineA.Id, Crew(f.Lan, f.Lan), default);
        var none = await Service(f, Owner()).SaveLineAsync(f.Plan.Id, f.LineA.Id, new SaveHarvestPlanLineStaffingRequest(null), default);

        Assert.Equal(ErrorKind.Validation, twice.Error!.Kind);
        Assert.Equal(ErrorKind.Validation, none.Error!.Kind);
        Assert.Empty(f.Store.Rows);
    }

    [Fact]
    public async Task The_crew_chosen_for_a_line_is_what_the_harvest_record_suggests_for_the_raft_that_day()
    {
        var f = new Fixture();
        var owner = Owner();
        await Service(f, owner).SaveLineAsync(f.Plan.Id, f.LineA.Id, Crew(f.Minh, f.Lan), default);
        var (guard, _) = Access(owner);
        var crew = new HarvestCrewService(
            f.Store,
            StubProxy.Create<IRaftRepository>(h => h["GetByIdsAsync"] = _ => (IReadOnlyList<Raft>)new List<Raft> { f.RaftA }),
            guard,
            new FixedTimeProvider(Now));

        var suggestion = (await crew.SuggestAsync(f.RaftA.Id, null, default)).Value!;

        Assert.Equal(["Lan", "Minh"], suggestion.Employees.Select(e => e.Name));
        Assert.Equal(Today, suggestion.Date);
    }

    // ---- Who, and when ----

    [Theory]
    [InlineData(StatusIds.HarvestPlan.Draft, true, true)]
    [InlineData(StatusIds.HarvestPlan.Assigned, true, true)]
    [InlineData(StatusIds.HarvestPlan.Harvesting, true, true)]
    [InlineData(StatusIds.HarvestPlan.Completed, true, false)]
    [InlineData(StatusIds.HarvestPlan.Cancelled, true, false)]
    public async Task The_owner_chooses_the_crew_until_the_plan_is_over(int status, bool owner, bool allowed)
    {
        var f = new Fixture(status);
        Assert.True(owner);

        var result = await Service(f, Owner()).SaveLineAsync(f.Plan.Id, f.LineA.Id, Crew(f.Lan), default);

        Assert.Equal(allowed, result.IsSuccess);
        if (!allowed)
        {
            Assert.Equal(ErrorKind.Conflict, result.Error!.Kind);
            Assert.Equal(MessageCodes.Gen09, result.Error.Message.Code);
            Assert.Empty(f.Store.Rows);
        }
    }

    [Theory]
    [InlineData(StatusIds.HarvestPlan.Assigned, true)]
    [InlineData(StatusIds.HarvestPlan.Harvesting, true)]
    [InlineData(StatusIds.HarvestPlan.Completed, false)]
    [InlineData(StatusIds.HarvestPlan.Cancelled, false)]
    public async Task The_zone_manager_of_the_zone_chooses_the_crew_from_the_hand_over_until_the_plan_is_over(int status, bool allowed)
    {
        var f = new Fixture(status);

        var result = await Service(f, Manager(f.ZoneA.Id)).SaveLineAsync(f.Plan.Id, f.LineA.Id, Crew(f.Lan), default);

        Assert.Equal(allowed, result.IsSuccess);
        if (!allowed)
        {
            Assert.Equal(MessageCodes.Gen09, result.Error!.Message.Code);
        }
    }

    [Fact]
    public async Task A_zone_manager_never_sees_a_draft_so_it_cannot_staff_it()
    {
        var f = new Fixture(StatusIds.HarvestPlan.Draft);

        var result = await Service(f, Manager(f.ZoneA.Id)).SaveLineAsync(f.Plan.Id, f.LineA.Id, Crew(f.Lan), default);

        Assert.Equal(ErrorKind.NotFound, result.Error!.Kind);
        Assert.Empty(f.Store.Rows);
    }

    [Fact]
    public async Task A_zone_manager_cannot_staff_a_line_of_another_zone()
    {
        var f = new Fixture();

        var result = await Service(f, Manager(f.ZoneA.Id)).SaveLineAsync(f.Plan.Id, f.LineB.Id, Crew(f.Lan), default);

        Assert.Equal(ErrorKind.NotFound, result.Error!.Kind);
        Assert.Empty(f.Store.Rows);
    }

    [Fact]
    public async Task A_plan_that_does_not_exist_or_a_line_that_is_not_in_it_is_not_found()
    {
        var f = new Fixture();

        Assert.Equal(ErrorKind.NotFound, (await Service(f, Owner()).SaveLineAsync(Guid.NewGuid(), f.LineA.Id, Crew(f.Lan), default)).Error!.Kind);
        Assert.Equal(ErrorKind.NotFound, (await Service(f, Owner()).SaveLineAsync(f.Plan.Id, Guid.NewGuid(), Crew(f.Lan), default)).Error!.Kind);
    }

    [Fact]
    public async Task The_crew_of_a_plan_whose_day_has_passed_is_not_changed_but_the_same_crew_may_be_saved()
    {
        var f = new Fixture(day: Today.AddDays(-1));
        f.Assign(f.Lan, f.LineA);

        var changed = await Service(f, Owner()).SaveLineAsync(f.Plan.Id, f.LineA.Id, Crew(f.Lan, f.Minh), default);
        var same = await Service(f, Owner()).SaveLineAsync(f.Plan.Id, f.LineA.Id, Crew(f.Lan), default);

        Assert.Equal(MessageCodes.Gen09, changed.Error!.Message.Code);
        Assert.True(same.IsSuccess);
        Assert.False(same.Value!.CanEdit);
        Assert.Single(f.Store.Rows);
    }

    [Fact]
    public async Task Without_the_job_type_thu_hoach_in_use_the_crew_cannot_be_chosen_but_can_be_emptied()
    {
        var f = new Fixture();
        f.Assign(f.Lan, f.LineA);
        f.JobInCatalogue = null;

        var refused = await Service(f, Owner()).SaveLineAsync(f.Plan.Id, f.LineA.Id, Crew(f.Lan, f.Minh), default);
        var emptied = await Service(f, Owner()).SaveLineAsync(f.Plan.Id, f.LineA.Id, Crew(), default);

        Assert.Equal(MessageCodes.Gen09, refused.Error!.Message.Code);
        Assert.True(emptied.IsSuccess);
        Assert.Empty(f.Store.Rows);
    }

    // ---- Reading ----

    [Fact]
    public async Task The_owner_reads_every_line_and_a_zone_manager_only_the_lines_of_its_zones_with_what_it_may_edit()
    {
        var f = new Fixture();
        f.Assign(f.Lan, f.LineA);
        f.Assign(f.Nam, f.LineB);
        f.CheckIn(f.Lan, Today);

        var owner = (await Service(f, Owner()).GetAsync(f.Plan.Id, default)).Value!;
        var manager = (await Service(f, Manager(f.ZoneA.Id)).GetAsync(f.Plan.Id, default)).Value!;

        Assert.Equal(["B01", "B11"], owner.Lines.Select(l => l.RaftCode));
        Assert.True(owner.CanEdit);
        Assert.All(owner.Lines, l => Assert.True(l.CanEdit));
        Assert.Equal([1, 2], owner.Lines[0].LaneNumbers);
        Assert.Equal("KV01", owner.Lines[0].ZoneCode);
        Assert.True(owner.Lines[0].Employees.Single().CheckedIn);
        Assert.False(owner.Lines[1].Employees.Single().CheckedIn);
        Assert.Equal(["B01"], manager.Lines.Select(l => l.RaftCode));
        Assert.True(manager.CanEdit);
    }

    [Fact]
    public async Task Nobody_may_edit_the_crew_of_a_plan_that_is_over_but_everyone_who_sees_it_reads_it()
    {
        var f = new Fixture(StatusIds.HarvestPlan.Completed);
        f.Assign(f.Lan, f.LineA);

        var staffing = (await Service(f, Owner()).GetAsync(f.Plan.Id, default)).Value!;

        Assert.False(staffing.CanEdit);
        Assert.Single(staffing.Lines[0].Employees);
    }

    [Fact]
    public async Task A_zone_manager_does_not_read_a_draft()
    {
        var f = new Fixture(StatusIds.HarvestPlan.Draft);

        var result = await Service(f, Manager(f.ZoneA.Id)).GetAsync(f.Plan.Id, default);

        Assert.Equal(ErrorKind.NotFound, result.Error!.Kind);
    }

    // ---- Following the plan ----

    private static HashSet<Guid> Lines(params HarvestPlanLine[] lines) => lines.Select(l => l.Id).ToHashSet();

    [Fact]
    public async Task Cancelling_a_plan_removes_the_assignments_nobody_checked_in_on_and_unlinks_the_rest()
    {
        var f = new Fixture();
        var free = f.Assign(f.Lan, f.LineA);
        var worked = f.Assign(f.Minh, f.LineA);
        var past = f.Assign(f.Nam, f.LineB, Today.AddDays(-1));
        f.CheckIn(f.Minh, Today);

        var refused = await Service(f, Owner()).FollowAsync(f.Plan, f.Plan.PlannedDate, Lines(f.LineA, f.LineB), default);

        Assert.Null(refused);
        Assert.DoesNotContain(free, f.Store.Rows);
        Assert.Contains(worked, f.Store.Rows);
        Assert.Contains(past, f.Store.Rows);
        Assert.Null(worked.HarvestPlanLineId);
        Assert.Null(past.HarvestPlanLineId);
        Assert.Single(f.Audit.Added, e => e.Action == "WorkAssignment.Removed");
    }

    [Fact]
    public async Task A_line_taken_out_of_the_plan_or_moved_to_another_raft_takes_its_crew_off_and_the_other_lines_keep_theirs()
    {
        var f = new Fixture();
        var gone = f.Assign(f.Lan, f.LineA);
        var stays = f.Assign(f.Nam, f.LineB);

        var refused = await Service(f, Owner()).FollowAsync(f.Plan, f.Plan.PlannedDate, Lines(f.LineA), default);

        Assert.Null(refused);
        Assert.DoesNotContain(gone, f.Store.Rows);
        Assert.Contains(stays, f.Store.Rows);
        Assert.Equal(f.LineB.Id, stays.HarvestPlanLineId);
    }

    [Fact]
    public async Task When_the_plan_moves_to_another_day_the_assignments_follow_except_those_checked_in()
    {
        var f = new Fixture();
        var moves = f.Assign(f.Lan, f.LineA);
        var stays = f.Assign(f.Minh, f.LineA);
        f.CheckIn(f.Minh, Today);

        var refused = await Service(f, Owner()).FollowAsync(f.Plan, Tomorrow, Lines(), default);

        Assert.Null(refused);
        Assert.Equal(Tomorrow, moves.Date);
        Assert.Equal(f.LineA.Id, moves.HarvestPlanLineId);
        Assert.Equal(Today, stays.Date);
        Assert.Null(stays.HarvestPlanLineId);
        Assert.Equal(2, f.Store.Rows.Count);
    }

    [Fact]
    public async Task A_plan_does_not_move_to_a_day_on_which_an_employee_of_its_crew_has_other_work_and_nothing_changes()
    {
        var f = new Fixture();
        var crew = f.Assign(f.Lan, f.LineA);
        f.Assign(f.Lan, null, Tomorrow, f.RaftA2);
        var other = f.Assign(f.Minh, f.LineA);

        var refused = await Service(f, Owner()).FollowAsync(f.Plan, Tomorrow, Lines(), default);

        Assert.Equal(ErrorKind.Conflict, refused!.Kind);
        Assert.Equal(MessageCodes.Gen09, refused.Message.Code);
        Assert.Equal(Today, crew.Date);
        Assert.Equal(Today, other.Date);
    }

    [Fact]
    public async Task A_plan_with_a_crew_does_not_move_to_a_day_that_has_passed()
    {
        var f = new Fixture();
        var crew = f.Assign(f.Lan, f.LineA);

        var refused = await Service(f, Owner()).FollowAsync(f.Plan, Today.AddDays(-1), Lines(), default);

        Assert.Equal(MessageCodes.Gen09, refused!.Message.Code);
        Assert.Equal(Today, crew.Date);
    }

    [Fact]
    public async Task A_plan_with_no_crew_follows_nothing()
    {
        var f = new Fixture();

        Assert.Null(await Service(f, Owner()).FollowAsync(f.Plan, Today.AddDays(-5), Lines(f.LineA), default));
        Assert.Empty(f.Audit.Added);
    }
}
