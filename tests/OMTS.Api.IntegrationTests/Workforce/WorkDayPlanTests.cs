using OMTS.Application.Features.Authorization.Models;
using System.Net;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using OMTS.Api.IntegrationTests.Infrastructure;
using OMTS.Application.Features.Authorization;
using OMTS.Application.Features.Sales.Requests.Dtos;
using OMTS.Application.Features.Sales.PurchaseRequests;
using OMTS.Application.Features.Workforce.Dtos;
using OMTS.Domain.Constants;
using OMTS.Domain.Entities.Farming;
using OMTS.Domain.Entities.Identity;
using OMTS.Domain.Entities.Labor;
using OMTS.Domain.Entities.Sales;
using OMTS.Domain.Enums;
using static OMTS.Api.IntegrationTests.Infrastructure.AcsTestKit;

namespace OMTS.Api.IntegrationTests.Workforce;

/// <summary>Yêu cầu 06/11: người được giao phải nhận kế hoạch trước; Owner không phân công thay Manager.</summary>
public sealed class WorkDayPlanTests(AuthApiFactory factory) : IClassFixture<AuthApiFactory>
{
    private static DateOnly Day => DateOnly.FromDateTime(DateTimeOffset.UtcNow.ToOffset(TimeSpan.FromHours(7)).DateTime).AddDays(2);

    [Fact]
    public async Task Owner_self_plan_assigns_and_employee_reads_actual_personal_work()
    {
        var s = await SetupAsync();
        var input = new SaveWorkDayPlan(s.Raft.Id, s.Job.Id, Day, s.OwnerAccount.Id, Guid.NewGuid());
        var plan = await CreateAsync(s.Owner, input);
        Assert.Equal("READY_TO_ASSIGN", plan.Status); Assert.True(plan.CanAssign); Assert.False(plan.CanRespond);
        var replay = await CreateAsync(s.Owner, input);
        Assert.Equal(plan.Id, replay.Id);
        await Success(await s.Owner.PostAsJsonAsync($"/api/work-plans/{plan.Id}/assignments", new AssignWorkDayPlan([s.Employee.Id], plan.RowVersion)));
        var mine = (await s.Worker.GetFromJsonAsync<MyWorkforceView>($"/api/me/workforce?from={Day:yyyy-MM-dd}&to={Day:yyyy-MM-dd}"))!;
        var row = Assert.Single(mine.Assignments);
        Assert.Equal(plan.Id, row.WorkDayPlanId); Assert.Equal(s.Job.Id, row.JobTypeId);
        Assert.Equal(HttpStatusCode.BadRequest, (await s.Owner.PutAsJsonAsync($"/api/work-plans/{plan.Id}", input with { Note = "Changed", RowVersion = (await GetAsync(s.Owner, plan.Id)).RowVersion })).StatusCode);
        Assert.Equal(1, await factory.QueryAsync(db => db.Set<WorkDayPlan>().CountAsync(p => p.CreatorAccountId == s.OwnerAccount.Id && p.ClientOperationId == input.ClientOperationId)));
    }

    [Fact]
    public async Task Delegated_plan_requires_manager_approval_and_only_that_manager_can_assign()
    {
        var s = await SetupAsync();
        var plan = await CreateAsync(s.Owner, new(s.Raft.Id, s.Job.Id, Day, s.ManagerAccount.Id, Guid.NewGuid()));
        Assert.Equal("PENDING_MANAGER", plan.Status); Assert.False(plan.CanAssign); Assert.False(plan.CanRespond);
        Assert.Equal(HttpStatusCode.Forbidden, (await s.Owner.PostAsJsonAsync($"/api/work-plans/{plan.Id}/assignments", new AssignWorkDayPlan([s.Employee.Id], plan.RowVersion))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await s.Manager.PostAsJsonAsync($"/api/work-plans/{plan.Id}/assignments", new AssignWorkDayPlan([s.Employee.Id], plan.RowVersion))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await s.Owner.PostAsJsonAsync($"/api/work-plans/{plan.Id}/response", new RespondWorkDayPlan(true, null, plan.RowVersion))).StatusCode);
        plan = await GetAsync(s.Manager, plan.Id); Assert.True(plan.CanRespond);
        var response = await s.Manager.PostAsJsonAsync($"/api/work-plans/{plan.Id}/response", new RespondWorkDayPlan(true, null, plan.RowVersion));
        await Success(response); plan = (await response.Content.ReadFromJsonAsync<WorkDayPlanView>())!;
        Assert.True(plan.CanAssign); Assert.Equal("READY_TO_ASSIGN", plan.Status);
        Assert.Equal(HttpStatusCode.Forbidden, (await s.Owner.PostAsJsonAsync("/api/workforce/assignments", new AssignWorkRequest(s.Raft.Id, s.Job.Id, Day, [s.Employee.Id], false, plan.Id))).StatusCode);
        await Success(await s.Manager.PostAsJsonAsync($"/api/work-plans/{plan.Id}/assignments", new AssignWorkDayPlan([s.Employee.Id], plan.RowVersion)));
        Assert.Single((await s.Worker.GetFromJsonAsync<MyWorkforceView>($"/api/me/workforce?from={Day:yyyy-MM-dd}&to={Day:yyyy-MM-dd}"))!.Assignments);
    }

    [Fact]
    public async Task Rejection_requires_reason_is_visible_to_owner_and_can_be_replanned()
    {
        var s = await SetupAsync();
        var input = new SaveWorkDayPlan(s.Raft.Id, s.Job.Id, Day, s.ManagerAccount.Id, Guid.NewGuid());
        var plan = await CreateAsync(s.Owner, input);
        Assert.Equal(HttpStatusCode.BadRequest, (await s.Manager.PostAsJsonAsync($"/api/work-plans/{plan.Id}/response", new RespondWorkDayPlan(false, " ", plan.RowVersion))).StatusCode);
        await Success(await s.Manager.PostAsJsonAsync($"/api/work-plans/{plan.Id}/response", new RespondWorkDayPlan(false, "Không có mặt tại vùng hôm đó", plan.RowVersion)));
        plan = await GetAsync(s.Owner, plan.Id);
        Assert.Equal("DECLINED", plan.Status); Assert.Equal("Không có mặt tại vùng hôm đó", plan.ResponseReason);
        Assert.Equal(HttpStatusCode.Forbidden, (await s.Manager.PostAsJsonAsync($"/api/work-plans/{plan.Id}/assignments", new AssignWorkDayPlan([s.Employee.Id], plan.RowVersion))).StatusCode);
        var edited = await s.Owner.PutAsJsonAsync($"/api/work-plans/{plan.Id}", input with { ResponsibleAccountId = s.OwnerAccount.Id, RowVersion = plan.RowVersion });
        await Success(edited); var updated = (await edited.Content.ReadFromJsonAsync<WorkDayPlanView>())!;
        Assert.True(updated.CanAssign); Assert.Equal("READY_TO_ASSIGN", updated.Status);
        Assert.Equal(1, await factory.QueryAsync(db => db.AuditEvents.CountAsync(a => a.RecordId == plan.Id && a.Action == "WorkDayPlan.Responded")));
    }

    [Fact]
    public async Task Other_manager_employee_and_trader_cannot_view_or_take_over_plan()
    {
        var s = await SetupAsync(); var outsider = await SetupAsync();
        var plan = await CreateAsync(s.Owner, new(s.Raft.Id, s.Job.Id, Day, s.ManagerAccount.Id, Guid.NewGuid()));
        Assert.Equal(HttpStatusCode.Forbidden, (await outsider.Manager.GetAsync($"/api/work-plans/{plan.Id}")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await s.Worker.GetAsync($"/api/work-plans/{plan.Id}")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await outsider.Manager.PostAsJsonAsync($"/api/work-plans/{plan.Id}/response", new RespondWorkDayPlan(true, null, plan.RowVersion))).StatusCode);
        var options = (await s.Owner.GetFromJsonAsync<List<WorkPlanResponsible>>($"/api/work-plans/responsibles?raftId={s.Raft.Id}"))!;
        Assert.Contains(options, o => o.Id == s.OwnerAccount.Id && o.IsSelf);
        Assert.Contains(options, o => o.Id == s.ManagerAccount.Id);
        Assert.DoesNotContain(options, o => o.Id == outsider.ManagerAccount.Id || o.Id == s.Employee.AccountId);
        Assert.Equal(HttpStatusCode.BadRequest, (await s.Owner.PostAsJsonAsync("/api/work-plans", new SaveWorkDayPlan(s.Raft.Id, s.Job.Id, Day, outsider.ManagerAccount.Id, Guid.NewGuid()))).StatusCode);
        var createOnly = await factory.AddAccountAsync(NewCode(), Password, SubjectType.Staff);
        var createOnlyRole = await factory.RoleAsync(StaffRole.Accountant);
        var zone = await factory.QueryAsync(db => db.FarmingZones.SingleAsync(z => z.Id == s.Raft.ZoneId));
        await factory.GrantAsync(createOnly, StaffRole.Accountant, zone);
        Assert.Equal(HttpStatusCode.BadRequest, (await s.Owner.PostAsJsonAsync("/api/work-plans", new SaveWorkDayPlan(s.Raft.Id, s.Job.Id, Day, createOnly.Id, Guid.NewGuid()))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await s.Owner.PostAsJsonAsync("/api/workforce/assignments", new AssignWorkRequest(s.Raft.Id, s.Job.Id, Day, [s.Employee.Id]))).StatusCode);
    }

    [Fact]
    public async Task Purchase_plan_routes_invitations_through_manager_then_owner_acceptance_activates_work()
    {
        var s = await SetupAsync();
        var trader = await factory.AddAccountAsync(NewCode(), Password, SubjectType.Trader);
        var coop = await factory.EnsureCooperativeAsync();
        var customer = new Customer { CooperativeId = coop.Id, AccountId = trader.Id, Name = NewCode() };
        var product = new Product { Code = NewCode(), Name = NewCode() };
        await factory.UseDbAsync(db => { db.Customers.Add(customer); db.Products.Add(product); });
        var buyer = await factory.LoginAsync(trader.Username);
        var submitted = await buyer.PostAsJsonAsync("/api/me/purchase-requests", new PlacePurchaseRequest(product.Id, 20, Day, null, Guid.NewGuid()));
        await Success(submitted); var orderId = (await submitted.Content.ReadFromJsonAsync<PurchaseRequestSaved>())!.Request.Id; var order = (await s.Owner.GetFromJsonAsync<PurchaseRequestView>($"/api/purchase-requests/{orderId}"))!;
        var plan = await CreateAsync(s.Owner, new(s.Raft.Id, s.Job.Id, Day, s.ManagerAccount.Id, Guid.NewGuid(), order.Id, 20, 20, Day));
        var inviteInput = new PlanPurchaseStaffing(20, Day, Day, 20, [s.Employee.Id], null, order.RowVersion);
        Assert.Equal(HttpStatusCode.Forbidden, (await s.Owner.PostAsJsonAsync($"/api/purchase-requests/{order.Id}/staffing", inviteInput)).StatusCode);
        await Success(await s.Manager.PostAsJsonAsync($"/api/work-plans/{plan.Id}/response", new RespondWorkDayPlan(true, null, plan.RowVersion)));
        plan = await GetAsync(s.Manager, plan.Id);
        await Success(await s.Manager.PostAsJsonAsync($"/api/work-plans/{plan.Id}/assignments", new AssignWorkDayPlan([s.Employee.Id], plan.RowVersion)));
        Assert.False(await factory.QueryAsync(db => db.WorkAssignments.AnyAsync(a => a.EmployeeId == s.Employee.Id)));
        var invitation = Assert.Single((await s.Worker.GetFromJsonAsync<List<MyPurchaseShiftView>>($"/api/me/purchase-shifts?from={Day:yyyy-MM-dd}&to={Day:yyyy-MM-dd}"))!);
        await Success(await s.Worker.PostAsJsonAsync($"/api/me/purchase-shifts/{invitation.Id}/response", new RespondPurchaseShift(true, null, invitation.RowVersion)));
        Assert.Equal(1, await factory.QueryAsync(db => db.Notifications.CountAsync(n => n.RecipientAccountId == s.ManagerAccount.Id &&
            n.Type == "WORK_PLAN_STAFF_RESPONDED" && n.RelatedRecordCode == plan.Id.ToString())));
        Assert.Equal(1, await factory.QueryAsync(db => db.Notifications.CountAsync(n => n.RecipientAccountId == s.OwnerAccount.Id &&
            n.Type == "PURCHASE_SHIFT_RESPONDED" && n.RelatedRecordCode == order.Code)));
        order = (await s.Owner.GetFromJsonAsync<PurchaseRequestView>($"/api/purchase-requests/{order.Id}"))!;
        await factory.UseDbAsync(async db => (await db.Set<JobType>().SingleAsync(j => j.Id == s.Job.Id)).StatusId = StatusIds.Usage.Discontinued);
        Assert.Equal(HttpStatusCode.BadRequest, (await s.Owner.PostAsJsonAsync($"/api/purchase-requests/{order.Id}/accept", new AcceptPurchaseRequest(20, Day, false, null, order.RowVersion))).StatusCode);
        Assert.False(await factory.QueryAsync(db => db.WorkAssignments.AnyAsync(a => a.EmployeeId == s.Employee.Id)));
        await factory.UseDbAsync(async db => (await db.Set<JobType>().SingleAsync(j => j.Id == s.Job.Id)).StatusId = StatusIds.Usage.InUse);
        await Success(await s.Owner.PostAsJsonAsync($"/api/purchase-requests/{order.Id}/accept", new AcceptPurchaseRequest(20, Day, false, null, order.RowVersion)));
        var assignment = await factory.QueryAsync(db => db.WorkAssignments.SingleAsync(a => a.EmployeeId == s.Employee.Id));
        Assert.Equal(plan.Id, assignment.WorkDayPlanId);
        Assert.Equal(s.Job.Id, assignment.JobTypeId);
        Assert.Equal("ASSIGNED", (await GetAsync(s.Manager, plan.Id)).Status);
    }

    private async Task<(HttpClient Owner, Account OwnerAccount, HttpClient Manager, Account ManagerAccount, Raft Raft, JobType Job, Employee Employee, HttpClient Worker)> SetupAsync()
    {
        var owner = await factory.AddAccountAsync(NewCode(), Password);
        var manager = await factory.AddAccountAsync(NewCode(), Password, SubjectType.Staff);
        var employeeAccount = await factory.AddAccountAsync(NewCode(), Password, SubjectType.Staff, a => a.ZaloAccountId = NewCode());
        var zone = await factory.AddZoneAsync(NewCode());
        var role = await factory.RoleAsync(StaffRole.ZoneManager);
        await factory.GrantAsync(manager, StaffRole.ZoneManager, zone);
        var raft = new Raft { ZoneId = zone.Id, Code = NewCode() };
        var job = new JobType { Name = "Thu hoặc sửa bè " + NewCode() };
        var employee = new Employee { CooperativeId = (await factory.EnsureCooperativeAsync()).Id, AccountId = employeeAccount.Id, FullName = NewCode() };
        await factory.UseDbAsync(db => { db.Rafts.Add(raft); db.Set<JobType>().Add(job); db.Employees.Add(employee); });
        return (await factory.LoginAsync(owner.Username), owner, await factory.LoginAsync(manager.Username), manager,
            raft, job, employee, await factory.LoginAsync(employeeAccount.Username));
    }
    private async Task<WorkDayPlanView> CreateAsync(HttpClient owner, SaveWorkDayPlan input)
    {
        var response = await owner.PostAsJsonAsync("/api/work-plans", input); await Success(response);
        return (await response.Content.ReadFromJsonAsync<WorkDayPlanView>())!;
    }
    private static async Task<WorkDayPlanView> GetAsync(HttpClient client, Guid id) => (await client.GetFromJsonAsync<WorkDayPlanView>($"/api/work-plans/{id}"))!;
    private async Task Success(HttpResponseMessage response) => Assert.True(response.IsSuccessStatusCode,
        await response.Content.ReadAsStringAsync() + "\n" + factory.Logs.Lines.LastOrDefault(line => line.StartsWith("Error:")));
}
