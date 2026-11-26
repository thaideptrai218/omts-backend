using System.Net;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using OMTS.Api.IntegrationTests.Infrastructure;
using OMTS.Application.Features.Sales.PurchaseRequests;
using OMTS.Application.Features.Sales.Requests.Dtos;
using OMTS.Domain.Constants;
using OMTS.Domain.Entities.Identity;
using OMTS.Domain.Entities.Labor;
using OMTS.Domain.Entities.Sales;
using OMTS.Domain.Enums;
using static OMTS.Api.IntegrationTests.Infrastructure.AcsTestKit;

namespace OMTS.Api.IntegrationTests.Sales;

/// <summary>
/// Quyết định 25/11/2026 (trở lại quyết định 31/10/2026, thay "phân công ngay" ngày 24/11/2026): người phụ trách mời nhân viên, đơn chờ nhân viên;
/// mỗi người đồng ý hoặc từ chối ca; đủ người đồng ý thì Chủ HTX mới nhận đơn, không đủ thì mời thêm hoặc Chủ HTX từ chối đơn.
/// </summary>
public sealed class PurchaseStaffingTests(AuthApiFactory factory) : IClassFixture<AuthApiFactory>
{
    private static DateOnly Day => DateOnly.FromDateTime(DateTimeOffset.UtcNow.ToOffset(TimeSpan.FromHours(7)).DateTime).AddDays(2);

    [Fact]
    public async Task The_owner_can_look_up_staff_before_a_work_plan_exists_without_opening_the_request_to_managers()
    {
        var (owner, trader, row) = await SetupAsync();
        var crew = await CrewAsync(1);
        var options = (await owner.GetFromJsonAsync<List<StaffingEmployeeOption>>(
            $"/api/purchase-requests/staffing-employees?day={Day:yyyy-MM-dd}&purchaseRequestId={row.Id}"))!;
        Assert.True(options.Single(e => e.Id == crew[0].Employee.Id).Available);
        Assert.False(await factory.QueryAsync(db => db.Set<WorkDayPlan>().AnyAsync(p => p.PurchaseRequestId == row.Id)));
        Assert.False(await factory.QueryAsync(db => db.Set<PurchaseShiftInvitation>().AnyAsync(i => i.PurchaseRequestId == row.Id)));
        Assert.Equal(0, await AssignmentsAsync(crew));

        var account = await factory.AddAccountAsync(NewCode(), Password, SubjectType.Staff);
        await factory.GrantAsync(account, StaffRole.ZoneManager, await factory.AddZoneAsync(NewCode()));
        var manager = await factory.LoginAsync(account.Username);
        var route = $"/api/purchase-requests/staffing-employees?day={Day:yyyy-MM-dd}&purchaseRequestId={row.Id}";
        Assert.Equal(HttpStatusCode.Forbidden, (await manager.GetAsync(route)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await trader.GetAsync(route)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await owner.GetAsync(
            $"/api/purchase-requests/staffing-employees?day={Day:yyyy-MM-dd}&purchaseRequestId={Guid.NewGuid()}")).StatusCode);
    }

    [Fact]
    public async Task Hundred_cages_needs_five_agreements_before_the_owner_can_accept()
    {
        var (owner, trader, row) = await SetupAsync();
        // Without a staffing plan there is nothing to accept.
        Assert.Equal(HttpStatusCode.BadRequest, (await AcceptAsync(owner, row)).StatusCode);
        var crew = await CrewAsync(5);

        await PlanAsync(owner, row, crew.Select(c => c.Employee.Id).ToArray());

        row = await GetAsync(owner, row.Id);
        Assert.Equal(StatusIds.PurchaseRequest.AwaitingStaff, row.StatusId);
        Assert.Equal(5, row.Staffing!.RequiredEmployees); Assert.Equal(0, row.Staffing.ApprovedEmployees); Assert.False(row.Staffing.HasEnoughStaff);
        // Invited, not assigned: each employee gets an invitation to answer.
        Assert.Equal(0, await AssignmentsAsync(crew));
        foreach (var c in crew)
            Assert.Equal(1, await factory.QueryAsync(db => db.Notifications.CountAsync(n => n.RecipientAccountId == c.Employee.AccountId && n.Type == "PURCHASE_SHIFT_INVITED")));
        Assert.True((await InvitationAsync(crew[0].Client)).CanRespond);

        for (var i = 0; i < 4; i++) await ReplyAsync(crew[i].Client, true);
        row = await GetAsync(owner, row.Id);
        Assert.Equal(4, row.Staffing!.ApprovedEmployees); Assert.False(row.Staffing.HasEnoughStaff);
        Assert.Equal(HttpStatusCode.BadRequest, (await AcceptAsync(owner, row)).StatusCode);

        await ReplyAsync(crew[4].Client, true);
        row = await GetAsync(owner, row.Id);
        Assert.Equal(StatusIds.PurchaseRequest.AwaitingStaff, row.StatusId); Assert.True(row.Staffing!.HasEnoughStaff);
        Assert.Equal(0, await AssignmentsAsync(crew));
        // The cages and the pickup must be those of the plan.
        Assert.Equal(HttpStatusCode.BadRequest, (await owner.PostAsJsonAsync($"/api/purchase-requests/{row.Id}/accept",
            new AcceptPurchaseRequest(80, Day, false, null, row.RowVersion))).StatusCode);
        await Success(await AcceptAsync(owner, row));

        var result = (await trader.GetFromJsonAsync<TraderPurchaseRequestView>($"/api/me/purchase-requests/{row.Id}"))!;
        Assert.Equal(StatusIds.PurchaseRequest.Accepted, result.StatusId); Assert.Equal(100, result.Response!.AcceptedCages);
        var invitations = (await owner.GetFromJsonAsync<List<PurchaseShiftInvitationView>>($"/api/purchase-requests/{row.Id}/staffing"))!;
        Assert.Equal(5, invitations.Count); Assert.All(invitations, i => Assert.True(i.Approved));
        // Accepting makes the work assignments of the staff who agreed, and tells them.
        Assert.Equal(5, await AssignmentsAsync(crew));
        foreach (var c in crew)
            Assert.Equal(1, await factory.QueryAsync(db => db.Notifications.CountAsync(n => n.RecipientAccountId == c.Employee.AccountId && n.Type == "PURCHASE_SHIFT_CONFIRMED")));
        await Success(await trader.PostAsJsonAsync($"/api/me/purchase-requests/{row.Id}/cancel", new CancelPurchaseRequest("Đổi lịch", result.RowVersion)));
        Assert.False(await factory.QueryAsync(db => db.Set<PurchaseShiftInvitation>().AnyAsync(i => i.PurchaseRequestId == row.Id && i.ReservedWorkDate != null)));
        Assert.Equal(0, await AssignmentsAsync(crew));
    }

    [Fact]
    public async Task Refusals_keep_the_request_waiting_the_responsible_invites_more_or_the_owner_declines_with_a_reason()
    {
        var (owner, trader, row) = await SetupAsync(); var crew = await CrewAsync(6);
        await PlanAsync(owner, row, crew.Take(5).Select(c => c.Employee.Id).ToArray());
        await ReplyAsync(crew[0].Client, false, "Bận việc gia đình");
        for (var i = 1; i < 5; i++) await ReplyAsync(crew[i].Client, true);
        row = await GetAsync(owner, row.Id);
        Assert.Equal(StatusIds.PurchaseRequest.AwaitingStaff, row.StatusId); Assert.Equal(4, row.Staffing!.ApprovedEmployees);
        Assert.Equal(1, row.Staffing.DeclinedEmployees); Assert.False(row.Staffing.HasEnoughStaff);
        Assert.Equal(HttpStatusCode.BadRequest, (await AcceptAsync(owner, row)).StatusCode);
        // The owner hears of each answer with how far the staffing got.
        Assert.True(await factory.QueryAsync(db => db.Notifications.AnyAsync(n => n.Type == "PURCHASE_SHIFT_RESPONDED" && n.RelatedRecordCode == row.Code && n.Body != null && n.Body.Contains("Mới có 4/5 người đồng ý"))));

        await Success(await owner.PostAsJsonAsync($"/api/purchase-requests/{row.Id}/staffing", Plan(row, [crew[5].Employee.Id])));
        var answers = (await owner.GetFromJsonAsync<List<PurchaseShiftInvitationView>>($"/api/purchase-requests/{row.Id}/staffing"))!;
        Assert.Equal("Bận việc gia đình", answers.Single(i => i.EmployeeId == crew[0].Employee.Id).Reason);
        Assert.Null(answers.Single(i => i.EmployeeId == crew[5].Employee.Id).Approved);
        row = await GetAsync(owner, row.Id);
        Assert.Equal(HttpStatusCode.BadRequest, (await owner.PostAsJsonAsync($"/api/purchase-requests/{row.Id}/decline",
            new DeclinePurchaseRequest(" ", row.RowVersion))).StatusCode);

        await Success(await owner.PostAsJsonAsync($"/api/purchase-requests/{row.Id}/decline", new DeclinePurchaseRequest("Không đủ nhân viên cho ngày nhận", row.RowVersion)));

        var result = (await trader.GetFromJsonAsync<TraderPurchaseRequestView>($"/api/me/purchase-requests/{row.Id}"))!;
        Assert.Equal(StatusIds.PurchaseRequest.Declined, result.StatusId); Assert.Equal("Không đủ nhân viên cho ngày nhận", result.Response!.Note);
        Assert.False(await factory.QueryAsync(db => db.Set<PurchaseShiftInvitation>().AnyAsync(i => i.PurchaseRequestId == row.Id && i.ReservedWorkDate != null)));
        Assert.Equal(0, await AssignmentsAsync(crew));
        var pending = await InvitationAsync(crew[5].Client);
        Assert.False(pending.CanRespond);
        Assert.Equal(HttpStatusCode.BadRequest, (await crew[5].Client.PostAsJsonAsync($"/api/me/purchase-shifts/{pending.Id}/response",
            new RespondPurchaseShift(true, null, pending.RowVersion))).StatusCode);
        Assert.Equal(1, await factory.QueryAsync(db => db.Notifications.CountAsync(n => n.RecipientAccountId == crew[5].Employee.AccountId && n.Type == "PURCHASE_SHIFT_CLOSED")));
    }

    [Fact]
    public async Task Once_there_are_enough_no_one_else_can_agree_and_accepting_closes_the_unanswered_invitations()
    {
        var (owner, _, row) = await SetupAsync(20); var crew = await CrewAsync(2);
        await PlanAsync(owner, row, [crew[0].Employee.Id, crew[1].Employee.Id], 20, 20);
        await ReplyAsync(crew[0].Client, true);
        var late = await InvitationAsync(crew[1].Client);
        Assert.False(late.CanRespond);
        Assert.Equal(HttpStatusCode.BadRequest, (await crew[1].Client.PostAsJsonAsync($"/api/me/purchase-shifts/{late.Id}/response",
            new RespondPurchaseShift(true, null, late.RowVersion))).StatusCode);
        Assert.True(await factory.QueryAsync(db => db.Notifications.AnyAsync(n => n.Type == "PURCHASE_SHIFT_RESPONDED" && n.RelatedRecordCode == row.Code && n.Body != null && n.Body.Contains("Đã đủ 1/1 người, Chủ HTX có thể nhận đơn"))));

        await Success(await AcceptAsync(owner, await GetAsync(owner, row.Id)));

        Assert.Equal(1, await AssignmentsAsync(crew));
        Assert.Equal(1, await factory.QueryAsync(db => db.Notifications.CountAsync(n => n.RecipientAccountId == crew[0].Employee.AccountId && n.Type == "PURCHASE_SHIFT_CONFIRMED")));
        Assert.Equal(0, await factory.QueryAsync(db => db.Notifications.CountAsync(n => n.RecipientAccountId == crew[1].Employee.AccountId && n.Type == "PURCHASE_SHIFT_CONFIRMED")));
        Assert.Equal(1, await factory.QueryAsync(db => db.Notifications.CountAsync(n => n.RecipientAccountId == crew[1].Employee.AccountId && n.Type == "PURCHASE_SHIFT_CLOSED")));
    }

    [Fact]
    public async Task Employees_can_only_answer_their_own_invitation_and_retries_do_not_duplicate_notifications()
    {
        var (owner, trader, row) = await SetupAsync(); var crew = await CrewAsync(2);
        await PlanAsync(owner, row, [crew[0].Employee.Id]);
        var invite = await InvitationAsync(crew[0].Client);
        var body = new RespondPurchaseShift(true, null, invite.RowVersion);
        Assert.Equal(HttpStatusCode.Forbidden, (await crew[1].Client.PostAsJsonAsync($"/api/me/purchase-shifts/{invite.Id}/response", body)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await trader.PostAsJsonAsync($"/api/me/purchase-shifts/{invite.Id}/response", body)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await crew[0].Client.GetAsync($"/api/purchase-requests/{row.Id}/staffing")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await trader.PostAsJsonAsync($"/api/purchase-requests/{row.Id}/staffing", Plan(row, [crew[0].Employee.Id]))).StatusCode);
        await Success(await crew[0].Client.PostAsJsonAsync($"/api/me/purchase-shifts/{invite.Id}/response", body));
        await Success(await crew[0].Client.PostAsJsonAsync($"/api/me/purchase-shifts/{invite.Id}/response", body));
        Assert.Equal(1, await factory.QueryAsync(db => db.AuditEvents.CountAsync(a => a.RecordId == invite.Id && a.Action == "PurchaseShift.Responded")));
        Assert.Equal(HttpStatusCode.Conflict, (await crew[0].Client.PostAsJsonAsync($"/api/me/purchase-shifts/{invite.Id}/response", body with { Approved = false, Reason = "Đổi ý" })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await crew[0].Client.GetAsync($"/api/me/purchase-shifts?from={Day:yyyy-MM-dd}&to={Day.AddDays(63):yyyy-MM-dd}")).StatusCode);
    }

    [Fact]
    public async Task Same_employee_cannot_count_for_two_orders_on_one_day_and_cancellation_releases_the_day()
    {
        var (owner, trader, row) = await SetupAsync(); var (_, _, other) = await SetupAsync(); var crew = await CrewAsync(1);
        // An employee may be invited to two requests of the same day; the first agreement holds the day.
        await PlanAsync(owner, row, [crew[0].Employee.Id]); await PlanAsync(owner, other, [crew[0].Employee.Id]);
        var invitations = (await crew[0].Client.GetFromJsonAsync<List<MyPurchaseShiftView>>(Range))!;
        var first = invitations.Single(i => i.PurchaseRequestId == row.Id); var second = invitations.Single(i => i.PurchaseRequestId == other.Id);
        await Success(await crew[0].Client.PostAsJsonAsync($"/api/me/purchase-shifts/{first.Id}/response", new RespondPurchaseShift(true, null, first.RowVersion)));
        Assert.Equal(HttpStatusCode.BadRequest, (await crew[0].Client.PostAsJsonAsync($"/api/me/purchase-shifts/{second.Id}/response", new RespondPurchaseShift(true, null, second.RowVersion))).StatusCode);
        row = await GetAsync(owner, row.Id);
        await Success(await trader.PostAsJsonAsync($"/api/me/purchase-requests/{row.Id}/cancel", new CancelPurchaseRequest("Đổi lịch", row.RowVersion)));
        await Success(await crew[0].Client.PostAsJsonAsync($"/api/me/purchase-shifts/{second.Id}/response", new RespondPurchaseShift(true, null, second.RowVersion)));
        Assert.Equal(1, (await GetAsync(owner, other.Id)).Staffing!.ApprovedEmployees);
    }

    [Fact]
    public async Task Leave_inactive_accounts_and_left_employees_are_refused_when_the_staff_is_chosen()
    {
        var (owner, _, row) = await SetupAsync(); var crew = await CrewAsync(3);
        // An approved leave of LABOR-05 (OMTS-132) makes the employee unavailable that day.
        await factory.UseDbAsync(db => db.EmployeeRequests.Add(new EmployeeRequest
        {
            EmployeeId = crew[0].Employee.Id, Type = EmployeeRequestType.Leave, FromDate = Day, ToDate = Day, Reason = "Nghỉ phép",
            SentAt = DateTimeOffset.UtcNow, StatusId = StatusIds.EmployeeRequest.Approved, ClientOperationId = Guid.NewGuid(),
        }));
        await factory.UseDbAsync(db =>
        {
            db.Accounts.Single(a => a.Id == crew[1].Employee.AccountId).StatusId = StatusIds.Account.Locked;
            db.Employees.Single(e => e.Id == crew[2].Employee.Id).StatusId = StatusIds.Employee.Left;
        });
        var options = (await owner.GetFromJsonAsync<List<StaffingEmployeeOption>>($"/api/purchase-requests/staffing-employees?day={Day:yyyy-MM-dd}"))!;
        Assert.False(options.Single(e => e.Id == crew[0].Employee.Id).Available);
        Assert.False(options.Single(e => e.Id == crew[1].Employee.Id).Available);
        Assert.DoesNotContain(options, e => e.Id == crew[2].Employee.Id);

        foreach (var refused in crew)
            Assert.Equal(HttpStatusCode.BadRequest, await PlanAsync(owner, row, [refused.Employee.Id], expect: HttpStatusCode.BadRequest));
        Assert.Equal(0, await AssignmentsAsync(crew));
    }

    [Fact]
    public async Task An_employee_who_agreed_and_then_became_unavailable_is_rechecked_before_accepting()
    {
        var (owner, _, row) = await SetupAsync(20); var crew = await CrewAsync(1);
        await PlanAsync(owner, row, [crew[0].Employee.Id], 20, 20);
        await ReplyAsync(crew[0].Client, true);
        // Leave approved after the agreement (LABOR-05): the acceptance is refused until it no longer holds.
        await factory.UseDbAsync(db => db.EmployeeRequests.Add(new EmployeeRequest
        {
            EmployeeId = crew[0].Employee.Id, Type = EmployeeRequestType.Leave, FromDate = Day, ToDate = Day, Reason = "Nghỉ phép",
            SentAt = DateTimeOffset.UtcNow, StatusId = StatusIds.EmployeeRequest.Approved, ClientOperationId = Guid.NewGuid(),
        }));
        Assert.Equal(HttpStatusCode.BadRequest, (await AcceptAsync(owner, await GetAsync(owner, row.Id))).StatusCode);
        await factory.UseDbAsync(db => db.EmployeeRequests.Single(r => r.EmployeeId == crew[0].Employee.Id).StatusId = StatusIds.EmployeeRequest.Rejected);
        await factory.UseDbAsync(db => db.Accounts.Single(a => a.Id == crew[0].Employee.AccountId).StatusId = StatusIds.Account.Locked);
        Assert.Equal(HttpStatusCode.BadRequest, (await AcceptAsync(owner, await GetAsync(owner, row.Id))).StatusCode);
        await factory.UseDbAsync(db => db.Accounts.Single(a => a.Id == crew[0].Employee.AccountId).StatusId = StatusIds.Account.Active);

        await Success(await AcceptAsync(owner, await GetAsync(owner, row.Id)));

        Assert.Equal(1, await AssignmentsAsync(crew));
    }

    [Fact]
    public async Task Plan_is_immutable_after_invites_and_capacity_rounds_up_without_overflow()
    {
        var (owner, _, row) = await SetupAsync(); var crew = await CrewAsync(2);
        await PlanAsync(owner, row, [crew[0].Employee.Id], 21, 20);
        row = await GetAsync(owner, row.Id); Assert.Equal(2, row.Staffing!.RequiredEmployees);
        foreach (var invalid in new[] { Plan(row, [crew[1].Employee.Id]) with { WorkDate = Day.AddDays(1) },
            Plan(row, [crew[1].Employee.Id]) with { CagesPerEmployeePerDay = 0 },
            Plan(row, [crew[1].Employee.Id]) with { EmployeeIds = [crew[1].Employee.Id, crew[1].Employee.Id] } })
            Assert.Equal(HttpStatusCode.BadRequest, (await owner.PostAsJsonAsync($"/api/purchase-requests/{row.Id}/staffing", invalid)).StatusCode);
        // A refusal needs a reason.
        var invite = await InvitationAsync(crew[0].Client);
        Assert.Equal(HttpStatusCode.BadRequest, (await crew[0].Client.PostAsJsonAsync($"/api/me/purchase-shifts/{invite.Id}/response", new RespondPurchaseShift(false, " ", invite.RowVersion))).StatusCode);
        var (_, _, large) = await SetupAsync(int.MaxValue);
        await PlanAsync(owner, large, [crew[1].Employee.Id], int.MaxValue, int.MaxValue);
        Assert.Equal(1, (await GetAsync(owner, large.Id)).Staffing!.RequiredEmployees);
    }

    [Fact]
    public async Task A_zone_manager_is_offered_only_the_employees_of_its_zones_and_cannot_staff_another()
    {
        var (owner, _, row) = await SetupAsync(20); var crew = await CrewAsync(2);
        var zone = await factory.AddZoneAsync(NewCode()); var otherZone = await factory.AddZoneAsync(NewCode());
        await factory.UseDbAsync(db =>
        {
            db.Set<EmployeeWorkingZone>().Add(new EmployeeWorkingZone { EmployeeId = crew[0].Employee.Id, ZoneId = zone.Id });
            db.Set<EmployeeWorkingZone>().Add(new EmployeeWorkingZone { EmployeeId = crew[1].Employee.Id, ZoneId = otherZone.Id });
        });
        var manager = await factory.AddAccountAsync(NewCode(), Password, SubjectType.Staff);
        await factory.GrantAsync(manager, StaffRole.ZoneManager, zone);
        var raft = new OMTS.Domain.Entities.Farming.Raft { ZoneId = zone.Id, Code = NewCode() };
        var job = new JobType { Name = NewCode() };
        await factory.UseDbAsync(db => { db.Rafts.Add(raft); db.Set<JobType>().Add(job); });
        var client = await factory.LoginAsync(manager.Username);
        var created = await owner.PostAsJsonAsync("/api/work-plans", new OMTS.Application.Features.Workforce.Dtos.SaveWorkDayPlan(raft.Id, job.Id, Day, manager.Id, Guid.NewGuid(), row.Id, 20, 20, Day));
        await Success(created);
        var plan = (await created.Content.ReadFromJsonAsync<OMTS.Application.Features.Workforce.Dtos.WorkDayPlanView>())!;
        await Success(await client.PostAsJsonAsync($"/api/work-plans/{plan.Id}/response", new OMTS.Application.Features.Workforce.Dtos.RespondWorkDayPlan(true, null, plan.RowVersion)));
        plan = (await client.GetFromJsonAsync<OMTS.Application.Features.Workforce.Dtos.WorkDayPlanView>($"/api/work-plans/{plan.Id}"))!;

        var options = (await client.GetFromJsonAsync<List<StaffingEmployeeOption>>($"/api/work-plans/{plan.Id}/employees"))!;
        var refused = await client.PostAsJsonAsync($"/api/work-plans/{plan.Id}/assignments", new OMTS.Application.Features.Workforce.Dtos.AssignWorkDayPlan([crew[1].Employee.Id], plan.RowVersion));

        Assert.Contains(options, e => e.Id == crew[0].Employee.Id);
        Assert.DoesNotContain(options, e => e.Id == crew[1].Employee.Id);
        Assert.Equal(HttpStatusCode.BadRequest, refused.StatusCode);
        await Success(await client.PostAsJsonAsync($"/api/work-plans/{plan.Id}/assignments", new OMTS.Application.Features.Workforce.Dtos.AssignWorkDayPlan([crew[0].Employee.Id], plan.RowVersion)));
        // Invited, not yet assigned: the employee answers first.
        Assert.True((await InvitationAsync(crew[0].Client)).CanRespond);
        Assert.Equal(0, await factory.QueryAsync(db => db.WorkAssignments.CountAsync(a => a.Date == Day && a.EmployeeId == crew[0].Employee.Id)));
    }

    private Task<int> AssignmentsAsync(List<(Employee Employee, HttpClient Client)> crew)
    {
        var ids = crew.Select(c => c.Employee.Id).ToList();
        return factory.QueryAsync(db => db.WorkAssignments.CountAsync(a => a.Date == Day && ids.Contains(a.EmployeeId)));
    }

    private static string Range => $"/api/me/purchase-shifts?from={Day:yyyy-MM-dd}&to={Day:yyyy-MM-dd}";
    private async Task<(HttpClient Owner, HttpClient Trader, PurchaseRequestView Row)> SetupAsync(int cages = 100)
    {
        var owner = await factory.AddAccountAsync(NewCode(), Password);
        var buyer = await factory.AddAccountAsync(NewCode(), Password, SubjectType.Trader);
        var customer = new Customer { AccountId = buyer.Id, CooperativeId = (await factory.EnsureCooperativeAsync()).Id, Name = NewCode() };
        var product = new Product { Code = NewCode(), Name = NewCode() };
        await factory.UseDbAsync(db => { db.Customers.Add(customer); db.Products.Add(product); });
        var trader = await factory.LoginAsync(buyer.Username);
        var response = await trader.PostAsJsonAsync("/api/me/purchase-requests", new PlacePurchaseRequest(product.Id, cages, Day, null, Guid.NewGuid()));
        await Success(response);
        var ownerClient = await factory.LoginAsync(owner.Username);
        return (ownerClient, trader, await GetAsync(ownerClient, (await response.Content.ReadFromJsonAsync<PurchaseRequestSaved>())!.Request.Id));
    }
    private async Task<List<(Employee Employee, HttpClient Client)>> CrewAsync(int count)
    {
        var result = new List<(Employee, HttpClient)>(); var cooperative = await factory.EnsureCooperativeAsync();
        for (var i = 0; i < count; i++)
        {
            var account = await factory.AddAccountAsync(NewCode(), Password, SubjectType.Staff, configure: a => a.ZaloAccountId = NewCode());
            var employee = new Employee { CooperativeId = cooperative.Id, FullName = NewCode(), AccountId = account.Id };
            await factory.UseDbAsync(db => db.Employees.Add(employee));
            result.Add((employee, await factory.LoginAsync(account.Username)));
        }
        return result;
    }
    private static PlanPurchaseStaffing Plan(PurchaseRequestView row, Guid[] ids, int cages = 100, int capacity = 20) => new(cages, Day, Day, capacity, ids, null, row.RowVersion);
    /// <summary>Makes the work plan of the request (once) and posts the staffing; answers the status of the staffing call, and fails the test when it is not <paramref name="expect"/> (OK by default).</summary>
    private async Task<HttpStatusCode> PlanAsync(HttpClient owner, PurchaseRequestView row, Guid[] ids, int cages = 100, int capacity = 20, HttpStatusCode expect = HttpStatusCode.OK)
    {
        var zone = await factory.AddZoneAsync(NewCode());
        var raft = new OMTS.Domain.Entities.Farming.Raft { ZoneId = zone.Id, Code = NewCode() };
        var job = new JobType { Name = NewCode() };
        await factory.UseDbAsync(db => { db.Rafts.Add(raft); db.Set<JobType>().Add(job); });
        await WorkPlanTestKit.SelfPlanAsync(owner, raft.Id, job.Id, Day, row.Id, cages, capacity, Day);

        row = await GetAsync(owner, row.Id);
        var response = await owner.PostAsJsonAsync($"/api/purchase-requests/{row.Id}/staffing", Plan(row, ids, cages, capacity));
        Assert.True(response.StatusCode == expect, await response.Content.ReadAsStringAsync());
        return response.StatusCode;
    }
    private static async Task<PurchaseRequestView> GetAsync(HttpClient owner, Guid id) => (await owner.GetFromJsonAsync<PurchaseRequestView>($"/api/purchase-requests/{id}"))!;
    private static Task<HttpResponseMessage> AcceptAsync(HttpClient owner, PurchaseRequestView row) => owner.PostAsJsonAsync($"/api/purchase-requests/{row.Id}/accept", new AcceptPurchaseRequest(row.Staffing?.Cages ?? row.RequestedCages, Day, false, null, row.RowVersion));
    private static async Task<MyPurchaseShiftView> InvitationAsync(HttpClient client) => Assert.Single((await client.GetFromJsonAsync<List<MyPurchaseShiftView>>(Range))!);
    private static async Task ReplyAsync(HttpClient client, bool approve, string? reason = null)
    {
        var invite = await InvitationAsync(client);
        await Success(await client.PostAsJsonAsync($"/api/me/purchase-shifts/{invite.Id}/response", new RespondPurchaseShift(approve, reason, invite.RowVersion)));
    }
    private static async Task Success(HttpResponseMessage response) => Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync());
}
