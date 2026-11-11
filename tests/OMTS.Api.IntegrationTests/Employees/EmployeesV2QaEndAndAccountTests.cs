using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using OMTS.Api.IntegrationTests.Infrastructure;
using OMTS.Application.Common;
using OMTS.Application.Common.Interfaces.Persistence;
using OMTS.Application.Features.Notifications;
using OMTS.Domain.Constants;
using OMTS.Domain.Entities.Identity;
using OMTS.Domain.Enums;
using OMTS.Infrastructure.Jobs;
using OMTS.Infrastructure.Persistence;
using OMTS.Infrastructure.Persistence.Repositories;
using static OMTS.Api.IntegrationTests.Employees.EmployeeRequests;
using static OMTS.Api.IntegrationTests.Employees.EmployeesV2QaKit;
using static OMTS.Api.IntegrationTests.Infrastructure.AcsTestKit;

namespace OMTS.Api.IntegrationTests.Employees;

/// <summary>QA of OMTS-227: end and resume (LABOR-01.3, 01.4), the daily job, staff account at add (LABOR-01.5), the removed routes.</summary>
public class EmployeesV2QaEndAndAccountTests(AuthApiFactory factory) : IClassFixture<AuthApiFactory>
{
    private Task<int> StatusOfAsync(Guid id) => factory.QueryAsync(db => db.Employees.Where(e => e.Id == id).Select(e => e.StatusId).SingleAsync());

    [Fact]
    public async Task The_job_catches_up_days_it_missed_and_leaves_resumed_and_later_employees_alone()
    {
        var (owner, zone, _) = await SetupAsync(factory);
        var today = Today();
        var missed = IdOf(await CreateAsync(owner, Profile($"Missed {NewCode()}", NewPhone(), zone.Id)));
        var resumed = IdOf(await CreateAsync(owner, Profile($"Resumed {NewCode()}", NewPhone(), zone.Id)));
        var stays = IdOf(await CreateAsync(owner, Profile($"Stays {NewCode()}", NewPhone(), zone.Id)));
        await EndAsync(owner, stays, today);
        await EndAsync(owner, resumed);
        await (await owner.PostAsJsonAsync($"/api/employees/{resumed}/resume", new { rowVersion = await CurrentVersionAsync(owner, resumed) })).JsonAsync(HttpStatusCode.OK);
        // The job did not run for days: Employed with a last working day long gone.
        await factory.UpdateAsync<Employee>(missed, e => e.LastWorkingDay = today.AddDays(-9));
        var job = factory.Services.GetRequiredService<EmploymentEndJob>();

        await job.RunOnceAsync(today);

        Assert.Equal(StatusIds.Employee.Left, await StatusOfAsync(missed));
        Assert.Equal(StatusIds.Employee.Working, await StatusOfAsync(resumed));
        Assert.Equal(StatusIds.Employee.Working, await StatusOfAsync(stays));
        Assert.Equal(1, await factory.QueryAsync(db => db.AuditEvents.CountAsync(e => e.RecordId == missed && e.Action == "Employee.StatusChanged" && e.ActorAccountId == null && e.ActorEmployeeId == null)));
        await job.RunOnceAsync(today);
        Assert.Equal(1, await factory.QueryAsync(db => db.AuditEvents.CountAsync(e => e.RecordId == missed && e.Action == "Employee.StatusChanged")));
    }

    // LABOR-01.3 step 5 sends one reminder for one end of employment: the day may be moved while still Employed.
    [Fact]
    public async Task L5_moving_the_last_working_day_does_not_send_a_second_reminder_with_the_old_date_left_standing()
    {
        using var host = new AuthApiFactory();
        var (owner, zone, _) = await SetupAsync(host);
        var staff = await host.AddAccountAsync(NewCode(), NewPassword(), SubjectType.Staff);
        var id = IdOf(await CreateAsync(owner, Profile($"Moved {NewCode()}", staff.PhoneNumber, zone.Id)));
        var today = Today();

        await EndAsync(owner, id, today);
        await EndAsync(owner, id, today.AddDays(-1));

        var rows = await host.QueryAsync(db => db.Notifications.AsNoTracking().Where(n => n.Type == NotificationTypes.StaffAccountLockReminder).ToListAsync());
        Assert.Single(rows);
    }

    // BR-LABOR-01: "their work assignments after the last working day are removed". An employee ended today is Employed and can
    // still be assigned (L4) until the job, and the job is the last moment: it does not go through the port.
    [Fact]
    public async Task L1_the_job_also_removes_the_assignments_after_the_last_working_day()
    {
        var records = new RecordingWorkRecords();
        await using var host = new WorkRecordsFactory(records);
        var (owner, zone, _) = await SetupAsync(host);
        var id = IdOf(await CreateAsync(owner, Profile($"Port {NewCode()}", NewPhone(), zone.Id)));
        var today = Today();
        await EndAsync(owner, id, today);
        records.RemovedAfter.Clear();

        Assert.Equal(1, await host.Services.GetRequiredService<EmploymentEndJob>().RunOnceAsync(today.AddDays(1)));

        Assert.Equal([(id, today)], records.RemovedAfter);
    }

    // The stub answers "no attendance" and "nothing to remove": a guard that passes silently once attendance exists is what must not ship.
    [Fact]
    public void The_empty_work_records_stub_is_gone_once_attendance_or_assignments_exist()
    {
        var sets = typeof(OmtsDbContext).GetProperties().Select(p => p.PropertyType).Where(t => t.IsGenericType).Select(t => t.GetGenericArguments()[0].Name).ToList();
        var stubHasNoDatabase = typeof(EmployeeWorkRecords).GetConstructors().All(c => c.GetParameters().Length == 0);

        Assert.False(stubHasNoDatabase && sets.Any(n => n is "Attendance" or "WorkAssignment"), "EmployeeWorkRecords must read Attendance and WorkAssignment now (LABOR-01.3.E1, step 4).");
    }

    // A last working day moved or ended twice: only one Left, one way back.
    [Fact]
    public async Task A_left_employee_cannot_be_ended_edited_or_given_a_zone_and_an_employed_one_cannot_be_resumed()
    {
        var (owner, zone, _) = await SetupAsync(factory);
        var (name, phone) = ($"States {NewCode()}", NewPhone());
        var id = IdOf(await CreateAsync(owner, Profile(name, phone, zone.Id)));
        var other = await factory.AddZoneAsync(NewCode());
        var today = Today();

        var ended = await EndAsync(owner, id, today);
        Assert.Equal("MSG-GEN-09", await (await owner.PostAsJsonAsync($"/api/employees/{id}/resume", new { rowVersion = RowVersionOf(ended) })).ErrorCodeAsync());

        var left = await EndAsync(owner, id, today.AddDays(-1));
        var version = RowVersionOf(left);
        Assert.Equal("MSG-GEN-09", await (await owner.PutAsJsonAsync($"/api/employees/{id}", Profile(name, phone, zone.Id).With("rowVersion", version))).ErrorCodeAsync());
        Assert.Equal("MSG-GEN-09", await (await owner.PostAsJsonAsync($"/api/employees/{id}/working-zones", new { zoneId = other.Id })).ErrorCodeAsync());
        Assert.Equal("MSG-GEN-09", await (await owner.PostEndAsync(id, new { lastWorkingDay = today.ToString("yyyy-MM-dd"), rowVersion = version })).ErrorCodeAsync());
    }

    // LABOR-01.5.E1, BR-ACCOUNT-02: "No two system accounts ... the same email, verified or not".
    [Fact]
    public async Task M2_an_email_of_another_account_is_refused_with_msg_account_09()
    {
        var (owner, zone, _) = await SetupAsync(factory);
        var email = $"{NewCode()}@example.test";
        await factory.AddAccountAsync(NewCode(), NewPassword(), SubjectType.Staff, a => a.Email = email);
        var (name, username) = ($"Email {NewCode()}", $"nv{NewCode()}");

        var response = await owner.PostAsJsonAsync("/api/employees", Profile(name, NewPhone(), zone.Id).With("account", new { username, email }));

        await response.Content.LoadIntoBufferAsync();
        Assert.True(response.StatusCode == HttpStatusCode.Conflict, $"{(int)response.StatusCode}: {await response.Content.ReadAsStringAsync()}");
        Assert.Equal(("MSG-ACCOUNT-09", "Email"), (await response.ErrorCodeAsync(), await FieldAsync(response)));
        Assert.False(await factory.QueryAsync(db => db.Employees.AnyAsync(e => e.FullName == name)));
        Assert.False(await factory.QueryAsync(db => db.Accounts.AnyAsync(a => a.Username == username)));
    }

    // LABOR-01.5.E2 and POST-4, end to end: nothing until a pair is granted, then the temporary password works.
    [Fact]
    public async Task The_account_logs_in_after_a_pair_is_granted_and_a_trader_number_is_refused()
    {
        var (owner, zone, role) = await SetupAsync(factory);
        var trader = await factory.AddAccountAsync(NewCode(), NewPassword(), SubjectType.Trader);
        var refused = await owner.PostAsJsonAsync("/api/employees", Profile($"Trader {NewCode()}", trader.PhoneNumber, zone.Id).With("account", new { username = $"nv{NewCode()}" }));
        Assert.Equal(("MSG-ACCOUNT-09", "Phone number"), (await refused.ErrorCodeAsync(), await FieldAsync(refused)));

        var username = $"nv{NewCode()}";
        var saved = await CreateAsync(owner, Profile($"Later pair {NewCode()}", NewPhone(), zone.Id).With("account", new { username }));
        var temporary = saved.GetProperty("temporaryPassword").GetString()!;
        Assert.Equal("MSG-ACCOUNT-07", await (await factory.CreateClient().PostAsJsonAsync("/api/auth/login", new { identifier = username, password = temporary })).ErrorCodeAsync());

        var account = await factory.QueryAsync(db => db.Accounts.AsNoTracking().SingleAsync(a => a.Username == username));
        await (await owner.PostAsJsonAsync($"/api/accounts/{account.Id}/grants", new { zoneId = zone.Id, role = role.Code })).JsonAsync(HttpStatusCode.Created);

        Assert.Equal(HttpStatusCode.OK, (await factory.CreateClient().PostAsJsonAsync("/api/auth/login", new { identifier = username, password = temporary })).StatusCode);
    }

    // The way to a staff account for an employee is for one who is Employed (client list §1, POST /{id}/account).
    [Fact]
    public async Task L9_a_left_employee_gets_no_account_through_the_accounts_route_either()
    {
        var (owner, zone, _) = await SetupAsync(factory);
        var left = IdOf(await CreateAsync(owner, Profile($"Left {NewCode()}", NewPhone(), zone.Id)));
        await EndAsync(owner, left);

        var created = await owner.PostAsJsonAsync("/api/accounts", new { displayName = "Nhân viên", username = $"nv{NewCode()}", phoneNumber = NewPhone(), employeeId = left });

        await created.Content.LoadIntoBufferAsync();
        Assert.True(created.StatusCode == HttpStatusCode.BadRequest, $"{(int)created.StatusCode}: {await created.Content.ReadAsStringAsync()}");
    }

    // The link of POST /api/accounts is saved; its answer should say so.
    [Fact]
    public async Task L9_the_accounts_route_answers_the_employee_it_linked()
    {
        var (owner, zone, _) = await SetupAsync(factory);
        var phone = NewPhone();
        var id = IdOf(await CreateAsync(owner, Profile($"Link {NewCode()}", phone, zone.Id)));
        var username = $"nv{NewCode()}";

        var created = await (await owner.PostAsJsonAsync("/api/accounts", new { displayName = "Nhân viên", username, phoneNumber = phone, employeeId = id })).JsonAsync(HttpStatusCode.Created);

        var account = await factory.QueryAsync(db => db.Accounts.AsNoTracking().SingleAsync(a => a.Username == username));
        Assert.Equal(account.Id, await factory.QueryAsync(db => db.Employees.Where(e => e.Id == id).Select(e => e.AccountId).SingleAsync()));
        Assert.Equal(id.ToString(), created.GetProperty("account").GetProperty("employeeId").ToString());
    }

    // ACCOUNT-10 "Opening the record of an event shows the record as it is now": the new actions open their employee.
    [Fact]
    public async Task An_audit_row_of_the_employee_opens_for_the_owner()
    {
        var (owner, zone, _) = await SetupAsync(factory);
        var id = IdOf(await CreateAsync(owner, Profile($"Open {NewCode()}", NewPhone(), zone.Id)));
        await EndAsync(owner, id);
        var rows = await factory.QueryAsync(db => db.AuditEvents.Where(e => e.RecordId == id).Select(e => e.Id).ToListAsync());

        Assert.Equal(3, rows.Count);
        foreach (var row in rows)
        {
            await (await owner.GetAsync($"/api/audit-events/{row}/record")).JsonAsync(HttpStatusCode.OK);
        }
    }

    // BR-LABOR-01 and MSG-LABOR-02 match by phone number. Recorded for the question of the design (Q18): a linked account with another number gets no reminder.
    [Fact]
    public async Task A_linked_account_whose_number_differs_from_the_employee_gets_no_reminder()
    {
        using var host = new AuthApiFactory();
        var (owner, zone, _) = await SetupAsync(host);
        var (name, phone) = ($"Linked {NewCode()}", NewPhone());
        var saved = await CreateAsync(owner, Profile(name, phone, zone.Id).With("account", new { username = $"nv{NewCode()}" }));
        var edited = await (await owner.PutAsJsonAsync($"/api/employees/{IdOf(saved)}", Profile(name, null, zone.Id).With("rowVersion", RowVersionOf(saved)))).JsonAsync(HttpStatusCode.OK);
        Assert.False(edited.GetProperty("employee").GetProperty("hasStaffAccount").GetBoolean());
        Assert.Equal(JsonValueKind.Object, edited.GetProperty("employee").GetProperty("account").ValueKind);

        await EndAsync(owner, IdOf(saved));

        Assert.False(await host.QueryAsync(db => db.Notifications.AnyAsync(n => n.Type == NotificationTypes.StaffAccountLockReminder)));
    }

    // SRS "Left from the next day", read while the job has not run: recorded, as the status is the stored one.
    [Fact]
    public async Task Until_the_job_has_run_an_employee_past_the_last_working_day_reads_employed_and_keeps_the_number()
    {
        var (owner, zone, _) = await SetupAsync(factory);
        var phone = NewPhone();
        var id = IdOf(await CreateAsync(owner, Profile($"Pending {NewCode()}", phone, zone.Id)));
        await factory.UpdateAsync<Employee>(id, e => e.LastWorkingDay = Today().AddDays(-1));

        var view = await (await owner.GetAsync($"/api/employees/{id}")).JsonAsync(HttpStatusCode.OK);

        Assert.Equal("Employed", view.GetProperty("status").GetString());
        Assert.Equal("MSG-LABOR-03", await (await owner.PostAsJsonAsync("/api/employees", Profile($"Same {NewCode()}", phone, zone.Id))).ErrorCodeAsync());
    }

    // The job and the service agree on "today": the business date, UTC+7, whatever the UTC date is.
    [Theory]
    [InlineData("2026-11-05T16:59:00Z", "2026-11-05", 2, true)]
    [InlineData("2026-11-05T17:00:00Z", "2026-11-06", 1, false)]
    [InlineData("2026-11-05T17:01:00Z", "2026-11-06", 1440, true)]
    public void Today_and_the_run_of_the_job_follow_the_business_date(string utc, string businessDay, int minutesToNextRun, bool past)
    {
        var now = DateTimeOffset.Parse(utc);

        Assert.Equal(DateOnly.Parse(businessDay), new OMTS.Api.IntegrationTests.Farming.FixedClock(now).Today());
        Assert.Equal(TimeSpan.FromMinutes(minutesToNextRun), DailyJobHostedService.DelayUntilNextRun(now, new EmploymentEndOptions().RunAt));
        Assert.Equal(past, DailyJobHostedService.IsPastRunTime(now, new EmploymentEndOptions().RunAt));
    }

    // Demo data: the fake ranges only, so nothing in a demo can reach a real person or account.
    [Fact]
    public void The_demo_phones_and_bank_accounts_stay_in_their_fake_ranges()
    {
        var plans = OMTS.Infrastructure.Bootstrap.Demo.DemoEmployeePlans.All;

        Assert.All(plans.Where(p => p.Phone is not null), p => Assert.Matches(@"\A090000\d{4}\z", p.Phone!));
        Assert.All(plans, p => Assert.Matches(@"\A9999\d{8}\z", p.BankAccountNumber));
        Assert.Equal(plans.Count, plans.Select(p => p.BankAccountNumber).Distinct().Count());
        Assert.Single(plans, p => p.Phone is null);
    }

    // LABOR-01.5 step 4: "shows the temporary password once (MSG-ACCOUNT-23)".
    [Fact(Skip = "Waiting for the PM: should the answer with the temporary password name MSG-ACCOUNT-23, or may the client build the text from its fields?")]
    public async Task L11_the_answer_with_the_temporary_password_names_msg_account_23()
    {
        var (owner, zone, _) = await SetupAsync(factory);

        var saved = await CreateAsync(owner, Profile($"Msg {NewCode()}", NewPhone(), zone.Id).With("account", new { username = $"nv{NewCode()}" }));

        Assert.Contains("MSG-ACCOUNT-23", saved.GetRawText());
    }

    [Fact]
    public async Task The_removed_routes_and_fields_are_gone_and_a_staff_account_with_the_number_still_counts_without_a_link()
    {
        var (owner, zone, _) = await SetupAsync(factory);
        var staff = await factory.AddAccountAsync(NewCode(), NewPassword(), SubjectType.Staff);
        var saved = await CreateAsync(owner, Profile($"Removed {NewCode()}", staff.PhoneNumber, zone.Id).With("accountId", staff.Id));
        var id = IdOf(saved);

        Assert.Equal(HttpStatusCode.NotFound, (await owner.GetAsync("/api/employees/linkable-accounts")).StatusCode);
        Assert.Equal(HttpStatusCode.MethodNotAllowed, (await owner.DeleteAsync($"/api/employees/{id}/account")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await owner.PostAsJsonAsync($"/api/employees/{id}/reinstate", new { rowVersion = RowVersionOf(saved) })).StatusCode);
        Assert.Null(await factory.QueryAsync(db => db.Employees.Where(e => e.Id == id).Select(e => e.AccountId).SingleAsync()));
        Assert.True(saved.GetProperty("employee").GetProperty("hasStaffAccount").GetBoolean());
        var ended = await EndAsync(owner, id);
        Assert.False(ended.TryGetProperty("followUp", out _));
    }

    private sealed class RecordingWorkRecords : IEmployeeWorkRecords
    {
        public List<(Guid EmployeeId, DateOnly Day)> RemovedAfter { get; } = [];

        public Task<DateOnly?> LastAttendanceDayAsync(Guid employeeId, CancellationToken cancellationToken) => Task.FromResult<DateOnly?>(null);

        public Task RemoveAssignmentsAfterAsync(Guid employeeId, DateOnly day, CancellationToken cancellationToken)
        {
            RemovedAfter.Add((employeeId, day));
            return Task.CompletedTask;
        }

        public Task<int> RemoveAssignmentsAfterLastWorkingDaysAsync(CancellationToken cancellationToken) => Task.FromResult(0);
    }

    private sealed class WorkRecordsFactory(IEmployeeWorkRecords records) : AuthApiFactory
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            base.ConfigureWebHost(builder);
            builder.ConfigureTestServices(services =>
            {
                services.RemoveAll<IEmployeeWorkRecords>();
                services.AddSingleton(records);
            });
        }
    }
}
