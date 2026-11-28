using Microsoft.Extensions.Caching.Memory;
using OMTS.Application.Common.Concurrency;
using OMTS.Application.Common.Interfaces;
using OMTS.Application.Common.Interfaces.Persistence;
using OMTS.Application.Common.Results;
using OMTS.Application.Features.Audit;
using OMTS.Application.Features.Authorization;
using OMTS.Application.Features.Authorization.Models;
using OMTS.Application.Features.CashBook;
using OMTS.Application.Features.Labor.Costs;
using OMTS.Application.Features.Notifications;
using OMTS.Application.Features.WorkDayPlans;
using OMTS.Application.Features.Workforce;
using OMTS.Application.UnitTests.Fakes;
using OMTS.Application.UnitTests.Features.Purchasing;
using OMTS.Domain.Constants;
using OMTS.Domain.Entities.CashBook;
using OMTS.Domain.Entities.Farming;
using OMTS.Domain.Entities.Identity;
using OMTS.Domain.Entities.Labor;
using OMTS.Domain.Enums;

namespace OMTS.Application.UnitTests.Features.WorkDayPlans;

/// <summary>The people hired outside for the day of a plan: they count towards the people needed, paying a line makes one cash voucher, and the cost goes to the raft or the zone of the plan.</summary>
public sealed class WorkDayPlanOutsiderServiceTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 18, 9, 0, 0, TimeSpan.FromHours(7));

    private sealed class Catalog : IStatusCatalog
    {
        public string CodeOf(int statusId) => $"S{statusId}";

        public string DisplayNameOf(int statusId) => $"Status {statusId}";
    }

    private sealed class Harness
    {
        public FarmingZone Zone { get; } = new() { Code = "KV01", Name = "Khu 1" };
        public WorkDayPlan Plan { get; }
        public List<CashVoucher> Vouchers { get; } = [];
        public List<PlanOutsiderHire> RemovedHires { get; } = [];
        public FakeAuditEventRepository Audit { get; } = new();
        public int Saves { get; private set; }
        public WorkDayPlanOutsiderService Service { get; }

        public Harness(Func<FarmingZone, AccountAccess> callerOf, int required = 3)
        {
            var caller = callerOf(Zone);
            var account = new Account { DisplayName = "Chủ HTX" };
            var raft = new Raft { Code = "B01", ZoneId = Zone.Id, Zone = Zone };
            Plan = new WorkDayPlan
            {
                Code = "KNL000001", Kind = WorkDayPlanKind.Seeding, WorkDate = new DateOnly(2026, 11, 19), ZoneId = Zone.Id, Zone = Zone, RaftId = raft.Id, Raft = raft,
                RequiredPeople = required, ResponsibleAccountId = account.Id, ResponsibleAccount = account, CreatorAccount = account,
            };
            var cache = new PermissionCache(new MemoryCache(new MemoryCacheOptions()));
            cache.Set(cache.KeyFor(caller.AccountId), caller);
            var accounts = StubProxy.Create<IAccountRepository>(h =>
            {
                h["GetDisplayNamesAsync"] = _ => (IReadOnlyDictionary<Guid, string>)new Dictionary<Guid, string>();
            });
            var permissions = new PermissionService(accounts, StubProxy.Create<IAccessGrantRepository>(_ => { }), StubProxy.Create<IFarmingZoneRepository>(_ => { }), cache);
            var user = new FakeCurrentUser(caller.AccountId);
            var clock = new FixedTimeProvider(Now);
            var uow = StubProxy.Create<IUnitOfWork>(h =>
            {
                h["SaveChangesAsync"] = _ => ++Saves;
                h["MarkChanged"] = _ => null;
            });
            var audit = new AuditLogService(Audit, user, new Catalog(), clock);
            var plans = StubProxy.Create<IWorkDayPlanRepository>(h =>
            {
                h["GetForEditAsync"] = a => (Guid)a[0]! == Plan.Id ? Plan : null;
                h["AddOutsider"] = a =>
                {
                    if (!Plan.OutsiderHires.Contains((PlanOutsiderHire)a[0]!))
                    {
                        Plan.OutsiderHires.Add((PlanOutsiderHire)a[0]!);
                    }

                    return null;
                };
                h["RemoveOutsider"] = a => { RemovedHires.Add((PlanOutsiderHire)a[0]!); return null; };
                h["VoucherCodesAsync"] = a => (IReadOnlyDictionary<Guid, string>)Vouchers.ToDictionary(v => v.Id, v => v.Code);
            });
            var vouchers = StubProxy.Create<ICashVoucherRepository>(h =>
            {
                h["Add"] = a => { Vouchers.Add((CashVoucher)a[0]!); return null; };
                h["FindIdByCodeAsync"] = _ => (Guid?)null;
            });
            var access = new WorkDayPlanAccess(new WorkPlanAccessService(user, permissions), plans, accounts, StubProxy.Create<IZoneRecipientQuery>(_ => { }));
            var notifier = new WorkDayPlanNotifier(StubProxy.Create<INotificationDispatcher>(h => h["NotifyAsync"] = _ => null));
            Service = new WorkDayPlanOutsiderService(
                plans,
                vouchers,
                StubProxy.Create<ICashAccountRepository>(h => h["GetByIdsAsync"] = _ => (IReadOnlyDictionary<Guid, CashAccount>)new Dictionary<Guid, CashAccount>()),
                StubProxy.Create<ICashVoucherTypeRepository>(h => h["GetByIdsAsync"] = _ => (IReadOnlyDictionary<Guid, CashVoucherType>)new Dictionary<Guid, CashVoucherType>()),
                StubProxy.Create<IAccountRepository>(h => h["ListActiveInternalAsync"] = _ => (IReadOnlyList<Account>)new List<Account>()),
                new CashVoucherCodeService(StubProxy.Create<IDocumentNumberAllocator>(h => h["NextAsync"] = _ => 7L), vouchers),
                access,
                new WorkDayPlanViews(accounts, plans, access, clock),
                new WorkDayPlanSettler(notifier, audit, uow, user, clock),
                new RowVersionGuard(accounts, uow),
                user, audit, uow, clock);
        }
    }

    private static AccountAccess Owner() => new(Guid.NewGuid(), SubjectType.CooperativeOwner, false, true, []);

    private static AccountAccess Manager(Guid zoneId) => new(
        Guid.NewGuid(), SubjectType.Staff, false, true,
        [new ZoneGrant(zoneId, Guid.NewGuid(), FixedRoles.ZoneManager.Code, FixedRoles.ZoneManager.Name, FixedRoles.ZoneManager.GrantedPermissions)]);

    [Fact]
    public async Task People_hired_outside_count_towards_the_people_needed()
    {
        var h = new Harness(_ => Owner(), required: 3);

        var partly = await h.Service.AddAsync(h.Plan.Id, new SaveOutsiderHireRequest(2, 300_000, "Tổ anh Ba", null), default);
        Assert.Equal((WorkDayPlanStatus.Staffing, 1), (partly.Value!.Status, partly.Value.Staffing.Missing));

        var filled = await h.Service.AddAsync(h.Plan.Id, new SaveOutsiderHireRequest(1, 250_000, null, "Phụ thả giống"), default);

        Assert.Equal((WorkDayPlanStatus.Ready, 3, 0), (filled.Value!.Status, filled.Value.Staffing.Outsiders, filled.Value.Staffing.Missing));
        Assert.Equal(2, filled.Value.Outsiders!.Count);

        var removed = await h.Service.RemoveAsync(h.Plan.Id, h.Plan.OutsiderHires.First().Id, default);
        Assert.Equal(WorkDayPlanStatus.Staffing, removed.Value!.Status);
    }

    [Theory]
    [InlineData(0, 300_000)]
    [InlineData(501, 300_000)]
    [InlineData(2, -1)]
    [InlineData(2, 100.5)]
    public async Task A_line_needs_one_to_five_hundred_people_and_a_whole_rate(int people, double rate)
    {
        var h = new Harness(_ => Owner());

        var result = await h.Service.AddAsync(h.Plan.Id, new SaveOutsiderHireRequest(people, (decimal)rate, null, null), default);

        Assert.Equal(ErrorKind.Validation, result.Error!.Kind);
        Assert.Empty(h.Plan.OutsiderHires);
    }

    [Fact]
    public async Task Paying_a_line_makes_one_payment_voucher_of_people_times_the_rate_and_a_paid_line_cannot_change()
    {
        var h = new Harness(_ => Owner());
        await h.Service.AddAsync(h.Plan.Id, new SaveOutsiderHireRequest(3, 300_000, "Tổ anh Ba", null), default);
        var hire = h.Plan.OutsiderHires.Single();

        var paid = await h.Service.PayAsync(h.Plan.Id, hire.Id, new PayOutsiderHireRequest(null, PaymentMethod.Cash), default);

        Assert.True(paid.IsSuccess);
        var voucher = Assert.Single(h.Vouchers);
        Assert.Equal(
            (CashDirection.Payment, CashFund.Cash, CashVoucherTypeIds.PaymentOutsiderLabour, 900_000m, CashPartyKind.Other, "Tổ anh Ba", new DateOnly(2026, 11, 18)),
            (voucher.Direction, voucher.Fund, voucher.TypeId, voucher.Amount, voucher.PartyKind, voucher.PartyName, voucher.VoucherDate));
        Assert.Equal((voucher.Id, voucher.Code), (hire.CashVoucherId, paid.Value!.Outsiders!.Single().CashVoucherCode));
        Assert.True(paid.Value.Outsiders!.Single().Paid);

        var again = await h.Service.PayAsync(h.Plan.Id, hire.Id, new PayOutsiderHireRequest(null, PaymentMethod.Cash), default);
        Assert.Equal("OUTSIDER_PAID", again.Error!.Details!["reasonCode"]);
        Assert.Single(h.Vouchers);

        var edited = await h.Service.UpdateAsync(h.Plan.Id, hire.Id, new SaveOutsiderHireRequest(2, 300_000, null, null), default);
        Assert.Equal(ErrorKind.Conflict, edited.Error!.Kind);
        var removed = await h.Service.RemoveAsync(h.Plan.Id, hire.Id, default);
        Assert.Equal(ErrorKind.Conflict, removed.Error!.Kind);
    }

    [Fact]
    public async Task A_zone_manager_adds_lines_but_does_not_pay_them()
    {
        var manager = new Harness(zone => Manager(zone.Id));
        var added = await manager.Service.AddAsync(manager.Plan.Id, new SaveOutsiderHireRequest(1, 300_000, null, null), default);
        Assert.True(added.IsSuccess);

        var paid = await manager.Service.PayAsync(manager.Plan.Id, manager.Plan.OutsiderHires.Single().Id, new PayOutsiderHireRequest(null, PaymentMethod.Cash), default);
        Assert.Equal(ErrorKind.Forbidden, paid.Error!.Kind);
        Assert.Empty(manager.Vouchers);
    }

    [Fact]
    public async Task A_payment_cannot_be_dated_in_the_future_or_use_an_unknown_method()
    {
        var h = new Harness(_ => Owner());
        await h.Service.AddAsync(h.Plan.Id, new SaveOutsiderHireRequest(1, 300_000, null, null), default);
        var hire = h.Plan.OutsiderHires.Single();

        var future = await h.Service.PayAsync(h.Plan.Id, hire.Id, new PayOutsiderHireRequest(Now.AddDays(1), PaymentMethod.Cash), default);
        var other = await h.Service.PayAsync(h.Plan.Id, hire.Id, new PayOutsiderHireRequest(null, PaymentMethod.Other), default);

        Assert.Equal(ErrorKind.Validation, future.Error!.Kind);
        Assert.Equal(ErrorKind.Validation, other.Error!.Kind);
        Assert.Empty(h.Vouchers);
    }

    // ---- Cost ----

    [Fact]
    public void People_hired_outside_cost_the_raft_of_the_plan_and_the_zone_of_a_nursery_plan()
    {
        var zone = new FarmingZone();
        var raft = new Raft { ZoneId = zone.Id };
        var seeding = new WorkDayPlan { Kind = WorkDayPlanKind.Seeding, WorkDate = new DateOnly(2026, 11, 19), ZoneId = zone.Id, RaftId = raft.Id };
        var nursery = new WorkDayPlan { Kind = WorkDayPlanKind.Nursery, WorkDate = new DateOnly(2026, 11, 19), ZoneId = zone.Id };
        var cancelled = new WorkDayPlan { Kind = WorkDayPlanKind.Seeding, Status = WorkDayPlanStatus.Cancelled, WorkDate = new DateOnly(2026, 11, 19), ZoneId = zone.Id, RaftId = raft.Id };
        var seedingJob = new JobType { Name = "Treo giống" };
        var hires = new[]
        {
            new PlanOutsiderHire { Plan = seeding, People = 3, RatePerSession = 300_000 },
            new PlanOutsiderHire { Plan = nursery, People = 2, RatePerSession = 200_000 },
            new PlanOutsiderHire { Plan = cancelled, People = 9, RatePerSession = 300_000 },
        };

        var pieces = LaborCostCalculator.OutsiderPieces(hires, new Dictionary<Guid, JobType> { [seedingJob.Id] = seedingJob });

        Assert.Equal(2, pieces.Count);
        var onRaft = pieces.Single(p => p.RaftId == raft.Id);
        Assert.Equal((zone.Id, 900_000m, 3m, seedingJob.Id, PayLineKind.Shift), (onRaft.ZoneId, onRaft.Amount, onRaft.Shifts, onRaft.JobTypeId, onRaft.Kind));
        var onZone = pieces.Single(p => p.RaftId is null);
        Assert.Equal((zone.Id, 400_000m, Guid.Empty), (onZone.ZoneId, onZone.Amount, onZone.JobTypeId));
        Assert.All(pieces, p => Assert.Equal(LaborCostCalculator.OutsiderEmployeeId, p.EmployeeId));
    }
}
