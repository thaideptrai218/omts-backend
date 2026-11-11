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

/// <summary>Quyết định 31/10/2026: đủ nhân viên đồng ý ca mới được nhận đơn; Owner quyết định.</summary>
public sealed class PurchaseStaffingTests(AuthApiFactory factory) : IClassFixture<AuthApiFactory>
{
    private static DateOnly Day => DateOnly.FromDateTime(DateTimeOffset.UtcNow.ToOffset(TimeSpan.FromHours(7)).DateTime).AddDays(2);

    [Fact]
    public async Task Hundred_cages_needs_five_distinct_approvals_and_owner_makes_the_final_decision()
    {
        var (owner, trader, row) = await SetupAsync();
        Assert.Equal(HttpStatusCode.BadRequest, (await AcceptAsync(owner, row)).StatusCode);
        var crew = await CrewAsync(5);
        await PlanAsync(owner, row, crew.Select(c => c.Employee.Id).ToArray());
        row = await GetAsync(owner, row.Id);
        Assert.Equal(StatusIds.PurchaseRequest.AwaitingStaff, row.StatusId); Assert.Equal(5, row.Staffing!.RequiredEmployees);
        for (var i = 0; i < 4; i++) await ReplyAsync(crew[i].Client, true);
        row = await GetAsync(owner, row.Id);
        Assert.Equal(4, row.Staffing!.ApprovedEmployees); Assert.False(row.Staffing.HasEnoughStaff);
        Assert.Equal(HttpStatusCode.BadRequest, (await AcceptAsync(owner, row)).StatusCode);
        await ReplyAsync(crew[4].Client, true);
        row = await GetAsync(owner, row.Id);
        Assert.Equal(StatusIds.PurchaseRequest.AwaitingStaff, row.StatusId); Assert.True(row.Staffing!.HasEnoughStaff);
        Assert.Equal(HttpStatusCode.BadRequest, (await owner.PostAsJsonAsync($"/api/purchase-requests/{row.Id}/accept",
            new AcceptPurchaseRequest(80, Day, false, null, row.RowVersion))).StatusCode);
        var accepted = await AcceptAsync(owner, row); await Success(accepted);
        var result = (await trader.GetFromJsonAsync<TraderPurchaseRequestView>($"/api/me/purchase-requests/{row.Id}"))!;
        Assert.Equal(StatusIds.PurchaseRequest.Accepted, result.StatusId); Assert.Equal(100, result.Response!.AcceptedCages);
        Assert.Equal(5, (await GetAsync(owner, row.Id)).Staffing!.ApprovedEmployees);
        var invitations = (await owner.GetFromJsonAsync<List<PurchaseShiftInvitationView>>($"/api/purchase-requests/{row.Id}/staffing"))!;
        Assert.Equal(5, invitations.Count); Assert.All(invitations, i => Assert.True(i.Approved));
        Assert.Equal(5, await factory.QueryAsync(db => db.Set<PurchaseShiftInvitation>().CountAsync(i => i.PurchaseRequestId == row.Id && i.ReservedWorkDate == Day)));
        await Success(await trader.PostAsJsonAsync($"/api/me/purchase-requests/{row.Id}/cancel", new CancelPurchaseRequest("Đổi lịch", result.RowVersion)));
        Assert.False(await factory.QueryAsync(db => db.Set<PurchaseShiftInvitation>().AnyAsync(i => i.PurchaseRequestId == row.Id && i.ReservedWorkDate != null)));
    }

    [Fact]
    public async Task Refusals_keep_order_pending_owner_can_invite_replacements_or_reject_with_reason()
    {
        var (owner, trader, row) = await SetupAsync(); var crew = await CrewAsync(6);
        await PlanAsync(owner, row, crew.Take(5).Select(c => c.Employee.Id).ToArray());
        await ReplyAsync(crew[0].Client, false, "Bận việc gia đình");
        for (var i = 1; i < 5; i++) await ReplyAsync(crew[i].Client, true);
        row = await GetAsync(owner, row.Id);
        Assert.Equal(StatusIds.PurchaseRequest.AwaitingStaff, row.StatusId); Assert.Equal(4, row.Staffing!.ApprovedEmployees);
        Assert.Equal(1, row.Staffing.DeclinedEmployees); Assert.False(row.Staffing.HasEnoughStaff);
        Assert.Equal(HttpStatusCode.BadRequest, (await AcceptAsync(owner, row)).StatusCode);
        await PlanAsync(owner, row, [crew[5].Employee.Id]);
        var options = (await owner.GetFromJsonAsync<List<PurchaseShiftInvitationView>>($"/api/purchase-requests/{row.Id}/staffing"))!;
        Assert.Equal("Bận việc gia đình", options.Single(i => i.EmployeeId == crew[0].Employee.Id).Reason);
        row = await GetAsync(owner, row.Id);
        Assert.Equal(HttpStatusCode.BadRequest, (await owner.PostAsJsonAsync($"/api/purchase-requests/{row.Id}/decline",
            new DeclinePurchaseRequest(" ", row.RowVersion))).StatusCode);
        var reject = await owner.PostAsJsonAsync($"/api/purchase-requests/{row.Id}/decline",
            new DeclinePurchaseRequest("Không đủ nhân viên cho ngày nhận", row.RowVersion));
        await Success(reject);
        var result = (await trader.GetFromJsonAsync<TraderPurchaseRequestView>($"/api/me/purchase-requests/{row.Id}"))!;
        Assert.Equal(StatusIds.PurchaseRequest.Declined, result.StatusId); Assert.Equal("Không đủ nhân viên cho ngày nhận", result.Response!.Note);
        Assert.False(await factory.QueryAsync(db => db.Set<PurchaseShiftInvitation>().AnyAsync(i => i.PurchaseRequestId == row.Id && i.ReservedWorkDate != null)));
        var pending = await InvitationAsync(crew[5].Client);
        Assert.False(pending.CanRespond);
        Assert.Equal(HttpStatusCode.BadRequest, (await crew[5].Client.PostAsJsonAsync($"/api/me/purchase-shifts/{pending.Id}/response",
            new RespondPurchaseShift(true, null, pending.RowVersion))).StatusCode);
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
    public async Task Leave_inactive_accounts_and_changed_employment_are_rechecked_before_accepting()
    {
        var (owner, _, row) = await SetupAsync(); var crew = await CrewAsync(5);
        await PlanAsync(owner, row, crew.Select(c => c.Employee.Id).ToArray());
        foreach (var c in crew) await ReplyAsync(c.Client, true);
        await factory.UseDbAsync(db => db.Set<EmployeeLeaveRequest>().Add(new EmployeeLeaveRequest
        { EmployeeId = crew[0].Employee.Id, From = Day, To = Day, Reason = "Nghỉ phép", Approved = true, ClientOperationId = Guid.NewGuid() }));
        row = await GetAsync(owner, row.Id);
        Assert.Equal(HttpStatusCode.BadRequest, (await AcceptAsync(owner, row)).StatusCode);
        var options = (await owner.GetFromJsonAsync<List<StaffingEmployeeOption>>($"/api/purchase-requests/staffing-employees?day={Day:yyyy-MM-dd}"))!;
        Assert.False(options.Single(e => e.Id == crew[0].Employee.Id).Available);
        await factory.UseDbAsync(db =>
        {
            var leave = db.Set<EmployeeLeaveRequest>().Single(r => r.EmployeeId == crew[0].Employee.Id);
            leave.Approved = false;
            db.Accounts.Single(a => a.Id == crew[1].Employee.AccountId).StatusId = StatusIds.Account.Locked;
        });
        Assert.Equal(HttpStatusCode.BadRequest, (await AcceptAsync(owner, await GetAsync(owner, row.Id))).StatusCode);
        await factory.UseDbAsync(db =>
        {
            db.Accounts.Single(a => a.Id == crew[1].Employee.AccountId).StatusId = StatusIds.Account.Active;
            db.Employees.Single(e => e.Id == crew[2].Employee.Id).StatusId = StatusIds.Employee.Left;
        });
        Assert.Equal(HttpStatusCode.BadRequest, (await AcceptAsync(owner, await GetAsync(owner, row.Id))).StatusCode);
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
        var invite = await InvitationAsync(crew[0].Client);
        Assert.Equal(HttpStatusCode.BadRequest, (await crew[0].Client.PostAsJsonAsync($"/api/me/purchase-shifts/{invite.Id}/response", new RespondPurchaseShift(false, " ", invite.RowVersion))).StatusCode);
        var (_, _, large) = await SetupAsync(int.MaxValue);
        await PlanAsync(owner, large, [crew[1].Employee.Id], int.MaxValue, int.MaxValue);
        Assert.Equal(1, (await GetAsync(owner, large.Id)).Staffing!.RequiredEmployees);
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
    private async Task PlanAsync(HttpClient owner, PurchaseRequestView row, Guid[] ids, int cages = 100, int capacity = 20)
    {
        var zone = await factory.AddZoneAsync(NewCode());
        var raft = new OMTS.Domain.Entities.Farming.Raft { ZoneId = zone.Id, Code = NewCode() };
        var job = new JobType { Name = NewCode() };
        await factory.UseDbAsync(db => { db.Rafts.Add(raft); db.Set<JobType>().Add(job); });
        await WorkPlanTestKit.SelfPlanAsync(owner, raft.Id, job.Id, Day, row.Id, cages, capacity, Day);
        row = await GetAsync(owner, row.Id);
        await Success(await owner.PostAsJsonAsync($"/api/purchase-requests/{row.Id}/staffing", Plan(row, ids, cages, capacity)));
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
