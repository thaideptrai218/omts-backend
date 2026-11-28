using Microsoft.Extensions.Caching.Memory;
using OMTS.Application.Common.Concurrency;
using OMTS.Application.Common.Interfaces;
using OMTS.Application.Common.Interfaces.Persistence;
using OMTS.Application.Common.Messages;
using OMTS.Application.Common.Results;
using OMTS.Application.Features.Audit;
using OMTS.Application.Features.Authorization;
using OMTS.Application.Features.Authorization.Models;
using OMTS.Application.Features.Employees;
using OMTS.Application.Features.Labor;
using OMTS.Application.Features.Labor.Work;
using OMTS.Application.Features.Notifications;
using OMTS.Application.Features.WorkDayPlans;
using OMTS.Application.Features.Workforce;
using OMTS.Application.UnitTests.Fakes;
using OMTS.Application.UnitTests.Features.Harvest;
using OMTS.Application.UnitTests.Features.Purchasing;
using OMTS.Domain.Constants;
using OMTS.Domain.Entities.Farming;
using OMTS.Domain.Entities.Harvest;
using OMTS.Domain.Entities.Identity;
using OMTS.Domain.Entities.Labor;
using OMTS.Domain.Enums;

namespace OMTS.Application.UnitTests.Features.WorkDayPlans;

/// <summary>
/// The people of a work-day plan with the real assignment rules: confirming on behalf gives the assignment of the day, taking a person off removes it (not after a check-in), the status follows the people,
/// and the plan of a harvest line follows the line.
/// </summary>
public sealed class WorkDayPlanStaffingServiceTests
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

    private sealed class Fixture
    {
        public FarmingZone Zone { get; } = new() { Code = "KV01", Name = "Khu 1", StatusId = StatusIds.Usage.InUse };
        public Raft Raft { get; }
        public Raft OtherRaft { get; }
        public Employee Lan { get; } = Worker("NV001", "Lan");
        public Employee Minh { get; } = Worker("NV002", "Minh");
        public Employee Nam { get; } = Worker("NV003", "Nam");
        public JobType Job { get; } = new() { Name = RecordCrewJobs.Seeding, StatusId = StatusIds.Usage.InUse };
        public HarvestPlanStaffingServiceTests.Store Store { get; }
        public WorkDayPlan Plan { get; }
        public List<WorkDayPlanMember> Removed { get; } = [];
        public List<string> Notices { get; } = [];
        public FakeAuditEventRepository Audit { get; } = new();
        public int Saves { get; private set; }

        public Fixture(int required, DateOnly? day = null)
        {
            Raft = new Raft { Code = "B01", ZoneId = Zone.Id, Zone = Zone, StatusId = StatusIds.Usage.InUse };
            OtherRaft = new Raft { Code = "B02", ZoneId = Zone.Id, Zone = Zone, StatusId = StatusIds.Usage.InUse };
            Store = new([Lan, Minh, Nam], [Raft, OtherRaft], Job);
            var account = new Account { DisplayName = "Quản lý vùng" };
            Plan = new WorkDayPlan
            {
                Code = "KNL000001",
                Kind = WorkDayPlanKind.Seeding,
                WorkDate = day ?? Tomorrow,
                ZoneId = Zone.Id,
                Zone = Zone,
                RaftId = Raft.Id,
                Raft = Raft,
                RequiredPeople = required,
                ResponsibleAccountId = account.Id,
                ResponsibleAccount = account,
                CreatorAccount = account,
            };
        }

        private static Employee Worker(string code, string name) => new() { Code = code, FullName = name, StatusId = StatusIds.Employee.Working };

        public WorkDayPlanMember Invited(Employee employee, DateTimeOffset? deadline = null)
        {
            var member = new WorkDayPlanMember { Plan = Plan, PlanId = Plan.Id, Employee = employee, EmployeeId = employee.Id, State = WorkDayMemberState.Invited, InvitedAt = Now, Deadline = deadline ?? Now.AddMinutes(30) };
            Plan.Members.Add(member);
            return member;
        }

        public IUnitOfWork Uow() => StubProxy.Create<IUnitOfWork>(h =>
        {
            h["SaveChangesAsync"] = _ => ++Saves;
            h["MarkChanged"] = _ => null;
            h["DiscardChanges"] = _ => null;
        });
    }

    private static AccountAccess Owner() => new(Guid.NewGuid(), SubjectType.CooperativeOwner, false, true, []);

    private static AccountAccess Manager(Guid zoneId) => new(
        Guid.NewGuid(), SubjectType.Staff, false, true,
        [new ZoneGrant(zoneId, Guid.NewGuid(), FixedRoles.ZoneManager.Code, FixedRoles.ZoneManager.Name, FixedRoles.ZoneManager.GrantedPermissions)]);

    private sealed record Services(WorkDayPlanStaffingService Staffing, WorkDayPlanHarvestLink Link, WorkDayPlanExpiryService Expiry);

    private static Services Build(Fixture f, AccountAccess caller)
    {
        var cache = new PermissionCache(new MemoryCache(new MemoryCacheOptions()));
        cache.Set(cache.KeyFor(caller.AccountId), caller);
        var accountsStub = StubProxy.Create<IAccountRepository>(h =>
        {
            h["GetDisplayNamesAsync"] = _ => (IReadOnlyDictionary<Guid, string>)new Dictionary<Guid, string>();
            h["GetByIdAsync"] = a => f.Plan.ResponsibleAccount;
        });
        var permissions = new PermissionService(accountsStub, StubProxy.Create<IAccessGrantRepository>(_ => { }), StubProxy.Create<IFarmingZoneRepository>(_ => { }), cache);
        var user = new FakeCurrentUser(caller.AccountId);
        var guard = new ZoneAccessGuard(user, permissions);
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
            StubProxy.Create<IEmployeeRepository>(h =>
            {
                h["AddWorkingZone"] = _ => null;
                h["GetForEditAsync"] = a => employees.FirstOrDefault(e => e.Id == (Guid)a[0]!);
            }),
            StubProxy.Create<IEmployeeRequestRepository>(_ => { }),
            StubProxy.Create<IRaftRepository>(h => h["GetByIdsAsync"] = a => (IReadOnlyList<Raft>)new[] { f.Raft, f.OtherRaft }.Where(r => ((IReadOnlyCollection<Guid>)a[0]!).Contains(r.Id)).ToList()),
            StubProxy.Create<IFarmingZoneRepository>(h => h["GetByIdsAsync"] = a => (IReadOnlyList<FarmingZone>)new[] { f.Zone }.Where(z => ((IReadOnlyCollection<Guid>)a[0]!).Contains(z.Id)).ToList()),
            guard, payPeriods, catalog, user, audit, uow, clock);
        var plansStub = StubProxy.Create<IWorkDayPlanRepository>(h =>
        {
            h["GetForEditAsync"] = a => (Guid)a[0]! == f.Plan.Id ? f.Plan : null;
            h["AddMember"] = a =>
            {
                var member = (WorkDayPlanMember)a[0]!;
                if (!f.Plan.Members.Contains(member))
                {
                    f.Plan.Members.Add(member);
                }

                return null;
            };
            h["RemoveMember"] = a =>
            {
                f.Removed.Add((WorkDayPlanMember)a[0]!);
                return null;
            };
            h["ListMembersOnDayAsync"] = _ => (IReadOnlyList<WorkDayPlanMember>)new List<WorkDayPlanMember>();
            h["VoucherCodesAsync"] = _ => (IReadOnlyDictionary<Guid, string>)new Dictionary<Guid, string>();
            h["ListPlanIdsWithOverdueInvitationsAsync"] = _ => (IReadOnlyList<Guid>)new List<Guid> { f.Plan.Id };
        });
        var access = new WorkDayPlanAccess(new WorkPlanAccessService(user, permissions), plansStub, accountsStub, StubProxy.Create<IZoneRecipientQuery>(_ => { }));
        var views = new WorkDayPlanViews(accountsStub, plansStub, access, clock);
        var notifier = new WorkDayPlanNotifier(StubProxy.Create<INotificationDispatcher>(h =>
            h["NotifyAsync"] = a => { f.Notices.Add(((NotificationContent)a[1]!).Title); return null; }));
        var settler = new WorkDayPlanSettler(notifier, audit, uow, user, clock);
        var staffing = new WorkDayPlanStaffingService(
            plansStub, f.Store, f.Store,
            StubProxy.Create<IEmployeeRepository>(h => h["GetForEditAsync"] = a => employees.FirstOrDefault(e => e.Id == (Guid)a[0]!)),
            StubProxy.Create<IEmployeeRequestRepository>(_ => { }),
            StubProxy.Create<IJobTypeRepository>(h => h["FindByNameAsync"] = _ => f.Job),
            new EmployeeCallerAccess(user, permissions, clock), work, access, views, settler, notifier, payPeriods,
            new RowVersionGuard(accountsStub, uow), user, audit, uow, clock);
        var crud = new WorkDayPlanCrudService(
            plansStub, null!, null!, null!, accountsStub, access, views, null!, staffing, settler, notifier, new RowVersionGuard(accountsStub, uow), user, audit, uow, clock);
        return new Services(staffing, new WorkDayPlanHarvestLink(plansStub, null!, access, staffing, crud, settler, user, audit, uow), new WorkDayPlanExpiryService(plansStub, notifier, audit, uow, clock));
    }

    private static ConfirmWorkDayMemberRequest Confirm(bool move = false) => new("Gọi điện xác nhận", move);

    // ---- Confirming ----

    [Fact]
    public async Task Confirming_for_a_person_gives_the_assignment_of_the_day_on_the_place_of_the_plan_and_fills_the_plan()
    {
        var f = new Fixture(required: 1);
        var lan = f.Invited(f.Lan);
        f.Minh.AccountId = Guid.NewGuid();
        var late = f.Invited(f.Minh);

        var result = await Build(f, Owner()).Staffing.ConfirmAsync(f.Plan.Id, lan.Id, Confirm(), default);

        Assert.True(result.IsSuccess);
        var assignment = Assert.Single(f.Store.Rows);
        Assert.Equal((f.Lan.Id, Tomorrow, f.Raft.Id, f.Zone.Id, f.Job.Id, f.Plan.Id), (assignment.EmployeeId, assignment.Date, assignment.RaftId, assignment.ZoneId, assignment.JobTypeId, assignment.WorkDayPlanId));
        Assert.Equal((WorkDayMemberState.ConfirmedOnBehalf, assignment.Id), (lan.State, lan.WorkAssignmentId));
        Assert.Equal("Gọi điện xác nhận", lan.Reason);
        Assert.Equal(WorkDayPlanStatus.Ready, f.Plan.Status);
        Assert.Equal(WorkDayMemberState.Unsuccessful, late.State);
        Assert.Equal(WorkDayPlanStatus.Ready, result.Value!.Status);
        Assert.Equal(1, f.Saves);
        Assert.Contains(f.Notices, n => n.Contains("Đã đủ người"));
        Assert.Contains(f.Notices, n => n.Contains("Lời mời không thành công"));
    }

    [Fact]
    public async Task A_plan_stays_short_until_the_last_person_is_confirmed()
    {
        var f = new Fixture(required: 2);
        var lan = f.Invited(f.Lan);
        var minh = f.Invited(f.Minh);
        var services = Build(f, Owner());

        await services.Staffing.ConfirmAsync(f.Plan.Id, lan.Id, Confirm(), default);
        Assert.Equal(WorkDayPlanStatus.Staffing, f.Plan.Status);
        Assert.Equal(WorkDayMemberState.Invited, minh.State);

        var done = await services.Staffing.ConfirmAsync(f.Plan.Id, minh.Id, Confirm(), default);

        Assert.Equal(WorkDayPlanStatus.Ready, done.Value!.Status);
        Assert.Equal(2, f.Store.Rows.Count);
    }

    [Fact]
    public async Task Confirming_needs_a_reason()
    {
        var f = new Fixture(required: 1);
        var lan = f.Invited(f.Lan);

        var result = await Build(f, Owner()).Staffing.ConfirmAsync(f.Plan.Id, lan.Id, new ConfirmWorkDayMemberRequest("  "), default);

        Assert.Equal(ErrorKind.Validation, result.Error!.Kind);
        Assert.Empty(f.Store.Rows);
        Assert.Equal(WorkDayMemberState.Invited, lan.State);
    }

    [Fact]
    public async Task A_person_on_another_raft_that_day_is_asked_about_and_moved_only_when_told_to()
    {
        var f = new Fixture(required: 1);
        var lan = f.Invited(f.Lan);
        f.Store.Add(new WorkAssignment { EmployeeId = f.Lan.Id, Date = Tomorrow, RaftId = f.OtherRaft.Id, ZoneId = f.Zone.Id, JobTypeId = f.Job.Id });
        var services = Build(f, Owner());

        var refused = await services.Staffing.ConfirmAsync(f.Plan.Id, lan.Id, Confirm(), default);

        Assert.Equal(MessageCodes.Labor06, refused.Error!.Message.Code);
        Assert.Equal(WorkDayMemberState.Invited, lan.State);
        Assert.Equal(f.OtherRaft.Id, f.Store.Rows.Single().RaftId);

        var moved = await services.Staffing.ConfirmAsync(f.Plan.Id, lan.Id, Confirm(move: true), default);

        Assert.True(moved.IsSuccess);
        var assignment = Assert.Single(f.Store.Rows);
        Assert.Equal((f.Raft.Id, f.Plan.Id), (assignment.RaftId, assignment.WorkDayPlanId));
    }

    [Fact]
    public async Task A_zone_manager_staffs_a_plan_of_its_zone_and_not_one_of_another_zone()
    {
        var f = new Fixture(required: 1);
        var lan = f.Invited(f.Lan);

        var denied = await Build(f, Manager(Guid.NewGuid())).Staffing.ConfirmAsync(f.Plan.Id, lan.Id, Confirm(), default);
        Assert.Equal(ErrorKind.NotFound, denied.Error!.Kind);

        var allowed = await Build(f, Manager(f.Zone.Id)).Staffing.ConfirmAsync(f.Plan.Id, lan.Id, Confirm(), default);
        Assert.True(allowed.IsSuccess);
    }

    // ---- Taking people off ----

    [Fact]
    public async Task Taking_a_confirmed_person_off_removes_the_assignment_and_makes_the_plan_short_again()
    {
        var f = new Fixture(required: 1);
        var lan = f.Invited(f.Lan);
        var services = Build(f, Owner());
        await services.Staffing.ConfirmAsync(f.Plan.Id, lan.Id, Confirm(), default);
        Assert.Equal(WorkDayPlanStatus.Ready, f.Plan.Status);

        var result = await services.Staffing.RemoveMemberAsync(f.Plan.Id, lan.Id, null, default);

        Assert.True(result.IsSuccess);
        Assert.Empty(f.Store.Rows);
        Assert.Empty(f.Plan.Members);
        Assert.Equal([lan], f.Removed);
        Assert.Equal(WorkDayPlanStatus.Staffing, f.Plan.Status);
        Assert.Contains(f.Notices, n => n.Contains("Chưa đủ người"));
        Assert.Single(f.Audit.Added, e => e.Action == "WorkAssignment.Removed");
    }

    [Fact]
    public async Task A_person_who_checked_in_cannot_be_taken_off()
    {
        var f = new Fixture(required: 1, day: Today);
        var lan = f.Invited(f.Lan);
        var services = Build(f, Owner());
        await services.Staffing.ConfirmAsync(f.Plan.Id, lan.Id, Confirm(), default);
        f.Store.Attendance.Add(new Attendance { EmployeeId = f.Lan.Id, Date = Today, CheckInAt = Now });

        var result = await services.Staffing.RemoveMemberAsync(f.Plan.Id, lan.Id, null, default);

        Assert.Equal(MessageCodes.Labor07, result.Error!.Message.Code);
        Assert.Single(f.Store.Rows);
        Assert.Single(f.Plan.Members);
        Assert.Equal(WorkDayPlanStatus.Ready, f.Plan.Status);
    }

    [Fact]
    public async Task Raising_the_people_needed_makes_a_ready_plan_short_and_lowering_it_fills_it()
    {
        var f = new Fixture(required: 1);
        var lan = f.Invited(f.Lan);
        var minh = f.Invited(f.Minh);
        var services = Build(f, Owner());
        await services.Staffing.ConfirmAsync(f.Plan.Id, lan.Id, Confirm(), default);

        var raised = await services.Staffing.SetRequiredPeopleAsync(f.Plan.Id, new SetWorkDayRequiredPeopleRequest(3, null), default);
        Assert.Equal((WorkDayPlanStatus.Staffing, 2), (raised.Value!.Status, raised.Value.Staffing.Missing));
        Assert.Equal(WorkDayMemberState.Unsuccessful, minh.State);

        var invalid = await services.Staffing.SetRequiredPeopleAsync(f.Plan.Id, new SetWorkDayRequiredPeopleRequest(0, null), default);
        Assert.Equal(ErrorKind.Validation, invalid.Error!.Kind);

        var lowered = await services.Staffing.SetRequiredPeopleAsync(f.Plan.Id, new SetWorkDayRequiredPeopleRequest(1, null), default);
        Assert.Equal(WorkDayPlanStatus.Ready, lowered.Value!.Status);
    }

    // ---- Inviting ----

    [Fact]
    public async Task Inviting_puts_people_on_the_plan_with_a_deadline_and_a_full_plan_takes_no_more()
    {
        var f = new Fixture(required: 1);
        var services = Build(f, Owner());

        var invited = await services.Staffing.InviteAsync(f.Plan.Id, new InviteWorkDayPlanRequest([f.Lan.Id, f.Minh.Id], 60), default);

        Assert.True(invited.IsSuccess);
        Assert.All(f.Plan.Members, m => Assert.Equal((WorkDayMemberState.Invited, Now.AddMinutes(60)), (m.State, m.Deadline)));
        Assert.Equal(2, f.Plan.Members.Count);

        await services.Staffing.ConfirmAsync(f.Plan.Id, f.Plan.Members.First(m => m.EmployeeId == f.Lan.Id).Id, Confirm(), default);
        var full = await services.Staffing.InviteAsync(f.Plan.Id, new InviteWorkDayPlanRequest([f.Nam.Id], null), default);

        Assert.Equal(ErrorKind.Conflict, full.Error!.Kind);
        Assert.Equal("PLAN_FULL", full.Error.Details!["reasonCode"]);
        Assert.DoesNotContain(f.Plan.Members, m => m.EmployeeId == f.Nam.Id);
    }

    [Theory]
    [InlineData(10)]
    [InlineData(0)]
    public async Task The_deadline_is_fifteen_thirty_or_sixty_minutes(int minutes)
    {
        var f = new Fixture(required: 1);

        var result = await Build(f, Owner()).Staffing.InviteAsync(f.Plan.Id, new InviteWorkDayPlanRequest([f.Lan.Id], minutes), default);

        Assert.Equal(ErrorKind.Validation, result.Error!.Kind);
        Assert.Empty(f.Plan.Members);
    }

    [Fact]
    public async Task An_invitation_that_ended_can_be_sent_again()
    {
        var f = new Fixture(required: 2);
        var expired = f.Invited(f.Lan);
        expired.State = WorkDayMemberState.Expired;
        var services = Build(f, Owner());

        await services.Staffing.InviteAsync(f.Plan.Id, new InviteWorkDayPlanRequest([f.Lan.Id], 15), default);

        Assert.Single(f.Plan.Members);
        Assert.Equal((WorkDayMemberState.Invited, Now.AddMinutes(15)), (expired.State, expired.Deadline));
    }

    // ---- Expiry ----

    [Fact]
    public async Task The_expiry_ends_overdue_invitations_and_tells_the_person_in_charge_how_many_are_missing()
    {
        var f = new Fixture(required: 3);
        var overdue = f.Invited(f.Lan, Now.AddMinutes(-2));
        var waiting = f.Invited(f.Minh, Now.AddMinutes(5));
        var services = Build(f, Owner());

        var expired = await services.Expiry.ExpirePlanAsync(f.Plan.Id, default);

        Assert.Equal(1, expired);
        Assert.Equal((WorkDayMemberState.Expired, WorkDayMemberState.Invited), (overdue.State, waiting.State));
        Assert.Single(f.Notices, n => n.Contains("Lời mời hết hạn"));
        Assert.Equal(0, await services.Expiry.ExpirePlanAsync(f.Plan.Id, default));
        Assert.Single(f.Notices, n => n.Contains("Lời mời hết hạn"));
    }

    // ---- Following the line of a harvest plan ----

    private static (HarvestPlan Harvest, HarvestPlanLine Line) HarvestOf(Fixture f)
    {
        var line = new HarvestPlanLine { LineNumber = 1, Raft = f.Raft, RaftId = f.Raft.Id, WorkDayPlan = f.Plan };
        f.Plan.HarvestPlanLine = line;
        f.Plan.HarvestPlanLineId = line.Id;
        f.Plan.Kind = WorkDayPlanKind.Harvest;
        return (new HarvestPlan { Code = "KHT000001", Lines = [line] }, line);
    }

    [Fact]
    public async Task The_plan_and_its_people_move_with_the_line_to_the_new_day_and_raft()
    {
        var f = new Fixture(required: 1);
        var lan = f.Invited(f.Lan);
        var services = Build(f, Owner());
        await services.Staffing.ConfirmAsync(f.Plan.Id, lan.Id, Confirm(), default);
        var (harvest, line) = HarvestOf(f);
        var newDate = Tomorrow.AddDays(2);

        var refused = await services.Link.FollowAsync(
            harvest, new DateTimeOffset(newDate.ToDateTime(new TimeOnly(7, 0)), TimeSpan.FromHours(7)), newDate, new Dictionary<Guid, Raft> { [line.Id] = f.OtherRaft }, default);

        Assert.Null(refused);
        Assert.Equal((newDate, f.OtherRaft.Id), (f.Plan.WorkDate, f.Plan.RaftId));
        var assignment = Assert.Single(f.Store.Rows);
        Assert.Equal((newDate, f.OtherRaft.Id, f.Plan.Id), (assignment.Date, assignment.RaftId, assignment.WorkDayPlanId));
    }

    [Fact]
    public async Task A_line_taken_out_cancels_its_plan_and_removes_the_assignments_of_people_who_did_not_check_in()
    {
        var f = new Fixture(required: 1);
        var lan = f.Invited(f.Lan);
        var services = Build(f, Owner());
        await services.Staffing.ConfirmAsync(f.Plan.Id, lan.Id, Confirm(), default);
        var (harvest, _) = HarvestOf(f);

        var refused = await services.Link.FollowAsync(harvest, Now, Tomorrow, new Dictionary<Guid, Raft>(), default);

        Assert.Null(refused);
        Assert.Equal(WorkDayPlanStatus.Cancelled, f.Plan.Status);
        Assert.Null(f.Plan.HarvestPlanLineId);
        Assert.Empty(f.Store.Rows);
        Assert.Null(lan.WorkAssignmentId);
    }

    [Fact]
    public async Task A_day_change_is_refused_when_someone_on_the_plan_checked_in()
    {
        var f = new Fixture(required: 1, day: Today);
        var lan = f.Invited(f.Lan);
        var services = Build(f, Owner());
        await services.Staffing.ConfirmAsync(f.Plan.Id, lan.Id, Confirm(), default);
        f.Store.Attendance.Add(new Attendance { EmployeeId = f.Lan.Id, Date = Today, CheckInAt = Now });
        var (harvest, line) = HarvestOf(f);

        var refused = await services.Link.FollowAsync(harvest, Now, Tomorrow, new Dictionary<Guid, Raft> { [line.Id] = f.Raft }, default);

        Assert.Equal(MessageCodes.Labor07, refused!.Message.Code);
        Assert.Equal(Today, f.Plan.WorkDate);
        Assert.Equal(Today, f.Store.Rows.Single().Date);
    }
}
