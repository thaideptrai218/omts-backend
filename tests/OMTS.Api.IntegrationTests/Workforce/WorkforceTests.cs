using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using OMTS.Api.IntegrationTests.Infrastructure;
using OMTS.Application.Features.Authorization;
using OMTS.Domain.Constants;
using OMTS.Domain.Entities.Farming;
using OMTS.Domain.Entities.Identity;
using OMTS.Domain.Entities.Labor;
using OMTS.Domain.Enums;
using static OMTS.Api.IntegrationTests.Infrastructure.AcsTestKit;
namespace OMTS.Api.IntegrationTests.Workforce;

public sealed class WorkforceTests(AuthApiFactory factory) : IClassFixture<AuthApiFactory>
{
    private static DateOnly Today => DateOnly.FromDateTime(DateTimeOffset.UtcNow.ToOffset(TimeSpan.FromHours(7)).DateTime);

    [Fact]
    public async Task Anonymous_is_rejected()
    {
        var response = await factory.CreateClient().GetAsync($"/api/me/workforce?from={Today:yyyy-MM-dd}&to={Today:yyyy-MM-dd}");
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Linked_employee_without_management_grants_reads_only_their_own_records()
    {
        var (owner, _, employee, raft, job) = await SetupAsync();
        var assigned = await owner.PostAsJsonAsync("/api/workforce/assignments", new { date = Today, raftId = raft.Id, jobTypeId = job.Id, employeeIds = new[] { employee.Id } });
        await Success(assigned);
        var client = await factory.LoginAsync(employee.Account!.Username);
        var mine = await client.GetFromJsonAsync<JsonElement>($"/api/me/workforce?from={Today:yyyy-MM-dd}&to={Today:yyyy-MM-dd}");
        Assert.Equal(employee.Id, mine.GetProperty("employeeId").GetGuid());
        Assert.Single(mine.GetProperty("assignments").EnumerateArray());
        Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync($"/api/workforce/assignments?day={Today:yyyy-MM-dd}")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await client.PostAsJsonAsync("/api/workforce/job-types", new { name = NewCode() })).StatusCode);
        var stranger = await factory.AddAccountAsync(NewCode(), Password, SubjectType.Staff, configure: a => a.ZaloAccountId = NewCode());
        var coop = await factory.EnsureCooperativeAsync();
        await factory.UseDbAsync(db => db.Employees.Add(new Employee { AccountId = stranger.Id, CooperativeId = coop.Id, FullName = NewCode() }));
        var other = await factory.LoginAsync(stranger.Username);
        var otherData = await other.GetFromJsonAsync<JsonElement>($"/api/me/workforce?from={Today:yyyy-MM-dd}&to={Today:yyyy-MM-dd}");
        Assert.Empty(otherData.GetProperty("assignments").EnumerateArray());
    }

    [Fact]
    public async Task Zone_manager_cannot_mutate_or_list_another_zone()
    {
        var (_, ownerAccount, employee, raft, job) = await SetupAsync();
        var zone = await factory.AddZoneAsync(NewCode());
        var manager = await factory.AddAccountAsync(NewCode(), Password, SubjectType.Staff);
        var role = await factory.AddRoleAsync(NewCode(), (PermissionFunctions.WorkAssignments, true, true, true), (PermissionFunctions.Attendance, true, true, true));
        await factory.GrantAsync(manager, role, zone);
        var client = await factory.LoginAsync(manager.Username);
        var response = await client.PostAsJsonAsync("/api/workforce/assignments", new { date = Today, raftId = raft.Id, jobTypeId = job.Id, employeeIds = new[] { employee.Id } });
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        var owner = await factory.LoginAsync(ownerAccount.Username);
        var added = await owner.PostAsJsonAsync("/api/workforce/assignments", new { date = Today, raftId = raft.Id, jobTypeId = job.Id, employeeIds = new[] { employee.Id } });
        await Success(added);
        var id = (await added.Content.ReadFromJsonAsync<JsonElement>())[0].GetProperty("id").GetGuid();
        Assert.Equal(HttpStatusCode.Forbidden, (await client.PostAsJsonAsync($"/api/workforce/assignments/{id}/check-in", new { })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await client.PostAsJsonAsync("/api/workforce/leave-requests/" + Guid.NewGuid() + "/review", new { approved = true, response = "OK", rowVersion = "" })).StatusCode);
        var listed = await client.GetFromJsonAsync<JsonElement>($"/api/workforce/assignments?day={Today:yyyy-MM-dd}");
        Assert.Empty(listed.EnumerateArray());
    }

    [Fact]
    public async Task Assignment_is_unique_and_cannot_be_replaced_after_check_in()
    {
        var (client, _, employee, raft, job) = await SetupAsync();
        var body = new { date = Today, raftId = raft.Id, jobTypeId = job.Id, employeeIds = new[] { employee.Id } };
        var first = await client.PostAsJsonAsync("/api/workforce/assignments", body);
        await Success(first);
        var id = (await first.Content.ReadFromJsonAsync<JsonElement>())[0].GetProperty("id").GetGuid();
        Assert.Equal(HttpStatusCode.Conflict, (await client.PostAsJsonAsync("/api/workforce/assignments", body)).StatusCode);
        await Success(await client.PostAsJsonAsync($"/api/workforce/assignments/{id}/check-in", new { }));
        Assert.Equal(HttpStatusCode.Conflict, (await client.PostAsJsonAsync($"/api/workforce/assignments/{id}/check-in", new { })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.DeleteAsync($"/api/workforce/assignments/{id}")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsJsonAsync("/api/workforce/assignments",
            new { date = Today, raftId = raft.Id, jobTypeId = job.Id, employeeIds = new[] { employee.Id }, moveExisting = true })).StatusCode);
        await Success(await client.PostAsJsonAsync($"/api/workforce/assignments/{id}/check-out", new { }));
        var records = await factory.QueryAsync(db => db.AttendanceRecords.Where(a => a.EmployeeId == employee.Id).Include(a => a.JobPeriods).ToListAsync());
        Assert.Single(records);
        Assert.NotNull(records[0].CheckedInAt); Assert.NotNull(records[0].CheckedOutAt); Assert.Single(records[0].JobPeriods);
    }

    [Fact]
    public async Task Checkout_without_checkin_is_incomplete_and_has_no_hours()
    {
        var (client, _, employee, raft, job) = await SetupAsync();
        var assigned = await client.PostAsJsonAsync("/api/workforce/assignments", new { date = Today, raftId = raft.Id, jobTypeId = job.Id, employeeIds = new[] { employee.Id } });
        await Success(assigned);
        var id = (await assigned.Content.ReadFromJsonAsync<JsonElement>())[0].GetProperty("id").GetGuid();
        await Success(await client.PostAsJsonAsync($"/api/workforce/assignments/{id}/check-out", new { }));
        var mine = await factory.LoginAsync(employee.Account!.Username);
        var view = await mine.GetFromJsonAsync<JsonElement>($"/api/me/workforce?from={Today:yyyy-MM-dd}&to={Today:yyyy-MM-dd}");
        var attendance = view.GetProperty("attendance")[0];
        Assert.False(attendance.GetProperty("complete").GetBoolean());
        Assert.Empty(attendance.GetProperty("jobs").EnumerateArray());
    }

    [Fact]
    public async Task Qr_binds_employee_expires_and_stores_only_hash()
    {
        var (client, _, employee, raft, job) = await SetupAsync();
        var mine = await factory.LoginAsync(employee.Account!.Username);
        var issued = await mine.PostAsJsonAsync("/api/me/attendance-qr", new { });
        await Success(issued);
        var qr = await issued.Content.ReadFromJsonAsync<JsonElement>();
        var code = qr.GetProperty("code").GetString()!;
        var stored = await factory.QueryAsync(db => db.Set<AttendanceQr>().SingleAsync(q => q.EmployeeId == employee.Id));
        Assert.DoesNotContain(code, stored.TokenHash);
        var assigned = await client.PostAsJsonAsync("/api/workforce/assignments", new { date = Today, raftId = raft.Id, jobTypeId = job.Id, employeeIds = new[] { employee.Id } });
        await Success(assigned);
        var id = (await assigned.Content.ReadFromJsonAsync<JsonElement>())[0].GetProperty("id").GetGuid();
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsJsonAsync($"/api/workforce/assignments/{id}/check-in", new { qrCode = "invalid" })).StatusCode);
        await factory.UpdateAsync<AttendanceQr>(stored.Id, q => q.ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(-1));
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsJsonAsync($"/api/workforce/assignments/{id}/check-in", new { qrCode = code })).StatusCode);
        issued = await mine.PostAsJsonAsync("/api/me/attendance-qr", new { });
        qr = await issued.Content.ReadFromJsonAsync<JsonElement>();
        await Success(await client.PostAsJsonAsync($"/api/workforce/assignments/{id}/check-in", new { qrCode = qr.GetProperty("code").GetString() }));
        Assert.Equal("QR", await factory.QueryAsync(db => db.AttendanceRecords.Where(a => a.EmployeeId == employee.Id).Select(a => a.CheckInMethod).SingleAsync()));
    }

    [Fact]
    public async Task Leave_is_idempotent_owner_review_notifies_and_is_shown_when_assigning()
    {
        var (owner, _, employee, raft, job) = await SetupAsync();
        var mine = await factory.LoginAsync(employee.Account!.Username);
        var body = new { from = Today.AddDays(1), to = Today.AddDays(2), reason = "Việc gia đình", clientOperationId = Guid.NewGuid() };
        var sent = await mine.PostAsJsonAsync("/api/me/leave-requests", body);
        await Success(sent);
        var row = await sent.Content.ReadFromJsonAsync<JsonElement>();
        var replay = await mine.PostAsJsonAsync("/api/me/leave-requests", body);
        await Success(replay);
        Assert.Equal(row.GetProperty("id").GetGuid(), (await replay.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid());
        var id = row.GetProperty("id").GetGuid();
        await Success(await owner.PostAsJsonAsync($"/api/workforce/leave-requests/{id}/review", new { approved = true, response = "Đồng ý", rowVersion = row.GetProperty("rowVersion").GetString() }));
        var lookups = await owner.GetFromJsonAsync<JsonElement>($"/api/workforce/lookups?day={Today.AddDays(1):yyyy-MM-dd}");
        Assert.True(lookups.GetProperty("employees").EnumerateArray().Single(e => e.GetProperty("id").GetGuid() == employee.Id).GetProperty("onLeave").GetBoolean());
        await Success(await owner.PostAsJsonAsync("/api/workforce/assignments", new { date = Today.AddDays(1), raftId = raft.Id, jobTypeId = job.Id, employeeIds = new[] { employee.Id } }));
        Assert.True(await factory.QueryAsync(db => db.Notifications.AnyAsync(n => n.RecipientAccountId == employee.AccountId && n.Type == "EMPLOYEE_LEAVE_REVIEWED")));
    }

    [Fact]
    public async Task First_zalo_link_of_employed_staff_needs_no_management_grant()
    {
        var coop = await factory.EnsureCooperativeAsync();
        var phone = NewPhone();
        var account = await factory.AddAccountAsync(NewCode(), Password, SubjectType.Staff, configure: a => a.PhoneNumber = phone);
        await factory.UseDbAsync(db => db.Employees.Add(new Employee { AccountId = account.Id, CooperativeId = coop.Id, FullName = NewCode() }));
        var response = await factory.CreateClient().PostAsJsonAsync("/api/auth/zalo/link", new { zaloAccessToken = "fake:" + NewCode(), phoneToken = "fake:" + phone, clientType = "Mobile" });
        await Success(response);
        var json = await response.Content.ReadFromJsonAsync<JsonElement>();
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", json.GetProperty("accessToken").GetString());
        var permissions = await client.GetFromJsonAsync<JsonElement>("/api/me/permissions");
        Assert.Empty(permissions.GetProperty("functions").EnumerateArray());
        Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync("/api/workforce/lookups")).StatusCode);
    }

    [Fact]
    public async Task Crew_change_splits_periods_and_keeps_prior_job_and_raft()
    {
        var (client, _, employee, raft, job) = await SetupAsync();
        var assigned = await client.PostAsJsonAsync("/api/workforce/assignments", new { date = Today, raftId = raft.Id, jobTypeId = job.Id, employeeIds = new[] { employee.Id } });
        await Success(assigned);
        var id = (await assigned.Content.ReadFromJsonAsync<JsonElement>())[0].GetProperty("id").GetGuid();
        await Success(await client.PostAsJsonAsync($"/api/workforce/assignments/{id}/check-in", new { }));
        var newJob = new JobType { Name = NewCode() };
        var newRaft = new Raft { ZoneId = raft.ZoneId, Code = NewCode() };
        await factory.UseDbAsync(db => { db.Set<JobType>().Add(newJob); db.Rafts.Add(newRaft); });
        await Success(await client.PostAsJsonAsync("/api/workforce/crew-job", new { raftId = newRaft.Id, jobTypeId = newJob.Id, employeeIds = new[] { employee.Id } }));
        var periods = await factory.QueryAsync(db => db.Set<AttendanceJobPeriod>().Where(p => p.Attendance.EmployeeId == employee.Id).OrderBy(p => p.StartsAt).ToListAsync());
        Assert.Equal(2, periods.Count);
        Assert.Equal(job.Id, periods[0].JobTypeId); Assert.Equal(raft.Id, periods[0].RaftId);
        Assert.Equal(newJob.Id, periods[1].JobTypeId); Assert.Equal(newRaft.Id, periods[1].RaftId);
        Assert.True(periods[1].StartsAt > periods[0].StartsAt);
    }

    [Fact]
    public async Task Attendance_only_grant_can_read_roster_but_cannot_assign_work()
    {
        var (owner, _, employee, raft, job) = await SetupAsync();
        await Success(await owner.PostAsJsonAsync("/api/workforce/assignments", new { date = Today, raftId = raft.Id, jobTypeId = job.Id, employeeIds = new[] { employee.Id } }));
        var staff = await factory.AddAccountAsync(NewCode(), Password, SubjectType.Staff);
        var role = await factory.AddRoleAsync(NewCode(), (PermissionFunctions.Attendance, true, true, false));
        var zone = await factory.QueryAsync(db => db.FarmingZones.SingleAsync(z => z.Id == raft.ZoneId));
        await factory.GrantAsync(staff, role, zone);
        var client = await factory.LoginAsync(staff.Username);
        await Success(await client.GetAsync("/api/workforce/lookups"));
        var rows = await client.GetFromJsonAsync<JsonElement>($"/api/workforce/assignments?day={Today:yyyy-MM-dd}");
        Assert.Single(rows.EnumerateArray());
        Assert.Equal(HttpStatusCode.Forbidden, (await client.PostAsJsonAsync("/api/workforce/assignments",
            new { date = Today, raftId = raft.Id, jobTypeId = job.Id, employeeIds = new[] { employee.Id } })).StatusCode);
    }

    [Fact]
    public async Task Settings_validate_break_and_never_change_a_locked_payroll()
    {
        var (client, owner, employee, _, _) = await SetupAsync();
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsJsonAsync("/api/workforce/attendance-settings",
            new { effectiveFrom = Today, breakFrom = "12:30:00", breakTo = "11:30:00", roundingMinutes = 15 })).StatusCode);
        await Success(await client.PostAsJsonAsync("/api/workforce/attendance-settings",
            new { effectiveFrom = Today.AddYears(3), roundingMinutes = 0 }));
        var locked = new LaborSettlement { EmployeeId = employee.Id, PeriodStart = Today.AddYears(4), PeriodEnd = Today.AddYears(4).AddMonths(1),
            StatusId = StatusIds.LaborSettlement.Approved, PreparedByAccountId = owner.Id };
        await factory.UseDbAsync(db => db.LaborSettlements.Add(locked));
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsJsonAsync("/api/workforce/attendance-settings",
            new { effectiveFrom = locked.PeriodStart, roundingMinutes = 15 })).StatusCode);
    }

    [Fact]
    public async Task Pending_leave_can_be_cancelled_only_by_its_employee_and_requested_again()
    {
        var (owner, _, employee, _, _) = await SetupAsync();
        var mine = await factory.LoginAsync(employee.Account!.Username);
        var body = new { from = Today.AddDays(5), to = Today.AddDays(6), reason = "Nghỉ phép", clientOperationId = Guid.NewGuid() };
        var sent = await mine.PostAsJsonAsync("/api/me/leave-requests", body);
        await Success(sent);
        var id = (await sent.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();
        var duplicate = await mine.PostAsJsonAsync("/api/me/leave-requests", new { body.from, body.to, body.reason, clientOperationId = Guid.NewGuid() });
        Assert.Equal(HttpStatusCode.Conflict, duplicate.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await owner.DeleteAsync($"/api/me/leave-requests/{id}")).StatusCode);
        var cancelled = await mine.DeleteAsync($"/api/me/leave-requests/{id}");
        await Success(cancelled);
        Assert.Equal(JsonValueKind.String, (await cancelled.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("cancelledAt").ValueKind);
        Assert.Equal(HttpStatusCode.Conflict, (await owner.PostAsJsonAsync($"/api/workforce/leave-requests/{id}/review",
            new { approved = true, response = "", rowVersion = "" })).StatusCode);
        await Success(await mine.PostAsJsonAsync("/api/me/leave-requests", new { body.from, body.to, body.reason, clientOperationId = Guid.NewGuid() }));
    }

    [Fact]
    public async Task Left_employee_without_grants_cannot_link_zalo()
    {
        var coop = await factory.EnsureCooperativeAsync();
        var phone = NewPhone();
        var account = await factory.AddAccountAsync(NewCode(), Password, SubjectType.Staff, configure: a => a.PhoneNumber = phone);
        await factory.UseDbAsync(db => db.Employees.Add(new Employee { AccountId = account.Id, CooperativeId = coop.Id, FullName = NewCode(), StatusId = StatusIds.Employee.Left }));
        var response = await factory.CreateClient().PostAsJsonAsync("/api/auth/zalo/link", new { zaloAccessToken = "fake:" + NewCode(), phoneToken = "fake:" + phone, clientType = "Mobile" });
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    private async Task<(HttpClient Client, Account Owner, Employee Employee, Raft Raft, JobType Job)> SetupAsync()
    {
        var coop = await factory.EnsureCooperativeAsync();
        var owner = await factory.AddAccountAsync(NewCode(), Password);
        var employeeAccount = await factory.AddAccountAsync(NewCode(), Password, SubjectType.Staff, configure: a => a.ZaloAccountId = NewCode());
        var employee = new Employee { CooperativeId = coop.Id, AccountId = employeeAccount.Id, FullName = NewCode() };
        var zone = await factory.AddZoneAsync(NewCode());
        var raft = new Raft { ZoneId = zone.Id, Code = NewCode() };
        var job = new JobType { Name = NewCode() };
        await factory.UseDbAsync(db => { db.Employees.Add(employee); db.Rafts.Add(raft); db.Set<JobType>().Add(job); });
        employee.Account = employeeAccount;
        return (await factory.LoginAsync(owner.Username), owner, employee, raft, job);
    }
    private static async Task Success(HttpResponseMessage response) => Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync());
}
