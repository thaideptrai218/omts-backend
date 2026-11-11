using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using OMTS.Api.IntegrationTests.Infrastructure;
using OMTS.Application.Common.Interfaces.Persistence;
using OMTS.Application.Features.Notifications;
using OMTS.Domain.Constants;
using OMTS.Domain.Enums;
using OMTS.Infrastructure.Jobs;
using static OMTS.Api.IntegrationTests.Employees.EmployeeRequests;
using static OMTS.Api.IntegrationTests.Infrastructure.AcsTestKit;

namespace OMTS.Api.IntegrationTests.Employees;

/// <summary>LABOR-01.3 end and LABOR-01.4 resume employment, the daily job, the reminder of MSG-LABOR-02, audit rows and the list.</summary>
public class EmployeeStatusAndAuditTests(AuthApiFactory factory) : IClassFixture<AuthApiFactory>
{
    // POST-3: "Left from the day after the last working day".
    [Fact]
    public async Task Ending_on_today_stays_employed_until_the_job_of_the_next_day_and_an_earlier_day_is_left_at_once()
    {
        var (owner, zone, _) = await SetupAsync(factory);
        var phone = NewPhone();
        var today = Today();
        var (todayId, earlierId) = (IdOf(await CreateAsync(owner, Profile($"Today {NewCode()}", phone, zone.Id))), IdOf(await CreateAsync(owner, Profile($"Earlier {NewCode()}", NewPhone(), zone.Id))));
        var job = factory.Services.GetRequiredService<EmploymentEndJob>();

        var endsToday = (await EndAsync(owner, todayId, today)).GetProperty("employee");
        var endsEarlier = (await EndAsync(owner, earlierId, today.AddDays(-3))).GetProperty("employee");

        Assert.Equal(("Employed", today.ToString("yyyy-MM-dd")), (endsToday.GetProperty("status").GetString(), endsToday.GetProperty("lastWorkingDay").GetString()));
        Assert.Equal(("Left", today.AddDays(-3).ToString("yyyy-MM-dd")), (endsEarlier.GetProperty("status").GetString(), endsEarlier.GetProperty("lastWorkingDay").GetString()));
        Assert.Equal("MSG-LABOR-03", await (await owner.PostAsJsonAsync("/api/employees", Profile($"Same {NewCode()}", phone, zone.Id))).ErrorCodeAsync());

        Assert.Equal(0, await job.RunOnceAsync(today));
        Assert.Equal(StatusIds.Employee.Working, await StatusOfAsync(todayId));

        Assert.Equal(1, await job.RunOnceAsync(today.AddDays(1)));
        Assert.Equal(0, await job.RunOnceAsync(today.AddDays(1)));
        Assert.Equal(StatusIds.Employee.Left, await StatusOfAsync(todayId));
        var automatic = await factory.QueryAsync(db => db.AuditEvents.AsNoTracking().SingleAsync(e => e.RecordId == todayId && e.Action == "Employee.StatusChanged"));
        Assert.Null(automatic.ActorAccountId);
        Assert.Contains(today.ToString("yyyy-MM-dd"), automatic.Reason);
        Assert.Equal(HttpStatusCode.Created, (await owner.PostAsJsonAsync("/api/employees", Profile($"Same {NewCode()}", phone, zone.Id))).StatusCode);
    }

    // LABOR-01.3.E1: not later than today; a last working day can still be moved earlier while the employee is Employed.
    [Fact]
    public async Task The_last_working_day_cannot_be_missing_or_in_the_future_and_can_be_moved_earlier_until_the_employee_is_left()
    {
        var (owner, zone, _) = await SetupAsync(factory);
        var id = IdOf(await CreateAsync(owner, Profile($"Date {NewCode()}", NewPhone(), zone.Id)));
        var today = Today();

        var missing = await owner.PostAsJsonAsync($"/api/employees/{id}/end-employment", new { rowVersion = await CurrentVersionAsync(owner, id) });
        var future = await owner.PostAsJsonAsync($"/api/employees/{id}/end-employment", await EndBodyAsync(owner, id, today.AddDays(1)));
        await EndAsync(owner, id, today);
        var moved = await EndAsync(owner, id, today.AddDays(-2));
        var again = await owner.PostAsJsonAsync($"/api/employees/{id}/end-employment", await EndBodyAsync(owner, id, today));

        Assert.Equal(("MSG-GEN-01", "Last working day"), (await missing.ErrorCodeAsync(), await FieldAsync(missing)));
        Assert.Equal(("MSG-GEN-01", "Last working day"), (await future.ErrorCodeAsync(), await FieldAsync(future)));
        Assert.Equal("Left", moved.GetProperty("employee").GetProperty("status").GetString());
        Assert.Equal("Employee is Left and cannot be used for this action.", (await again.SrsProblemAsync(HttpStatusCode.Conflict, "MSG-GEN-09", "MSG61")).Detail());
    }

    // LABOR-01.3.E1 and step 4 reach the attendance and assignments of later PRs through one port: here a stand-in holds them.
    [Fact]
    public async Task The_guard_reads_the_attendance_of_the_work_records_port_and_removes_later_assignments_through_it()
    {
        var records = new FakeWorkRecords();
        await using var host = (AuthApiFactory)new WorkRecordsApiFactory(records);
        var (owner, zone, _) = await SetupAsync(host);
        var id = IdOf(await CreateAsync(owner, Profile($"Guard {NewCode()}", NewPhone(), zone.Id)));
        var today = Today();
        records.LastAttendanceDay = today.AddDays(-2);

        var before = await owner.PostAsJsonAsync($"/api/employees/{id}/end-employment", await EndBodyAsync(owner, id, today.AddDays(-3)));

        Assert.Equal(("MSG-GEN-01", "Last working day"), (await before.ErrorCodeAsync(), await FieldAsync(before)));
        Assert.Empty(records.RemovedAfter);

        await EndAsync(owner, id, today.AddDays(-2));

        Assert.Equal([(id, today.AddDays(-2))], records.RemovedAfter);
    }

    // LABOR-01.3 step 5 and BR-LABOR-04: kept in the Owner's list and not pushed, matched by the phone number.
    [Fact]
    public async Task A_staff_account_with_the_phone_of_the_employee_reminds_the_owner_and_is_not_locked()
    {
        using var host = new AuthApiFactory();
        var (owner, zone, _) = await SetupAsync(host);
        var manager = await host.AddAccountAsync(NewCode(), Password, SubjectType.Staff);
        var locked = await host.AddAccountAsync(NewCode(), Password, SubjectType.Staff, a => a.StatusId = StatusIds.Account.Locked);
        var name = $"Reminder {NewCode()}";
        var withAccount = IdOf(await CreateAsync(owner, Profile(name, manager.PhoneNumber, zone.Id)));
        var withLocked = IdOf(await CreateAsync(owner, Profile($"Locked {NewCode()}", locked.PhoneNumber, zone.Id)));
        var plain = IdOf(await CreateAsync(owner, Profile($"Plain {NewCode()}", NewPhone(), zone.Id)));
        var noPhone = IdOf(await CreateAsync(owner, Profile($"No phone {NewCode()}", null, zone.Id)));
        var day = Today().AddDays(-1);

        foreach (var id in new[] { withLocked, plain, noPhone })
        {
            await EndAsync(owner, id, day);
        }

        Assert.False(await host.QueryAsync(db => db.Notifications.AnyAsync(n => n.Type == NotificationTypes.StaffAccountLockReminder)));

        await EndAsync(owner, withAccount, day);

        var reminder = await host.QueryAsync(db => db.Notifications.AsNoTracking().SingleAsync(n => n.Type == NotificationTypes.StaffAccountLockReminder));
        Assert.Equal("MSG-LABOR-02", reminder.MessageCode);
        Assert.Equal(
            $"{name} no longer works for the cooperative after {day:dd/MM/yyyy}. Staff account {manager.Username} has the same phone number: lock it in User accounts if it is no longer needed.",
            reminder.Body);
        Assert.Equal((RelatedRecordTypes.Account, manager.Id), (reminder.RelatedRecordType, reminder.RelatedRecordId));
        Assert.Equal(StatusIds.Account.Active, await host.QueryAsync(db => db.Accounts.Where(a => a.Id == manager.Id).Select(a => a.StatusId).SingleAsync()));
    }

    // LABOR-01.4: no new approval; MSG-LABOR-03 when another Employed employee has the number; another number or none.
    [Fact]
    public async Task Resume_needs_a_free_number_and_may_replace_or_clear_it()
    {
        var (owner, zone, _) = await SetupAsync(factory);
        var manager = await ManagerAsync(factory, zone);
        var phone = NewPhone();
        var id = IdOf(await CreateAsync(owner, Profile($"Back {NewCode()}", phone, zone.Id)));
        var keeps = IdOf(await CreateAsync(owner, Profile($"Keeps {NewCode()}", NewPhone(), zone.Id)));
        await EndAsync(owner, id);
        await EndAsync(owner, keeps);
        var holderName = $"Holder {NewCode()}";
        var holder = IdOf(await CreateAsync(owner, Profile(holderName, phone, zone.Id)));
        var version = await CurrentVersionAsync(owner, id);

        var taken = await manager.PostAsJsonAsync($"/api/employees/{id}/resume", new { rowVersion = version });
        var invalid = await manager.PostAsJsonAsync($"/api/employees/{id}/resume", new { rowVersion = version, phoneNumber = "123" });
        var problem = await taken.SrsProblemAsync(HttpStatusCode.Conflict, "MSG-LABOR-03", "MSG03");

        Assert.Equal(holder, problem.GetProperty("existingEmployeeId").GetGuid());
        Assert.Contains(holderName, problem.Detail());
        Assert.Equal(("MSG-GEN-01", "Phone number"), (await invalid.ErrorCodeAsync(), await FieldAsync(invalid)));
        Assert.Equal(StatusIds.Employee.Left, await StatusOfAsync(id));

        var cleared = (await (await manager.PostAsJsonAsync($"/api/employees/{id}/resume", new { rowVersion = version, phoneNumber = "" })).BodyAsync(HttpStatusCode.OK)).GetProperty("employee");
        var keptOwn = (await (await manager.PostAsJsonAsync($"/api/employees/{keeps}/resume", new { rowVersion = await CurrentVersionAsync(owner, keeps) })).BodyAsync(HttpStatusCode.OK)).GetProperty("employee");

        Assert.Equal(("Employed", JsonValueKind.Null, JsonValueKind.Null), (cleared.GetProperty("status").GetString(), cleared.GetProperty("phoneNumber").ValueKind, cleared.GetProperty("lastWorkingDay").ValueKind));
        Assert.Equal(JsonValueKind.String, keptOwn.GetProperty("phoneNumber").ValueKind);

        var notLeft = await manager.PostAsJsonAsync($"/api/employees/{id}/resume", new { rowVersion = version });
        Assert.Equal("Employee is Employed and cannot be used for this action.", (await notLeft.SrsProblemAsync(HttpStatusCode.Conflict, "MSG-GEN-09", "MSG61")).Detail());
    }

    [Fact]
    public async Task Status_changes_are_audited_with_the_person_who_acted()
    {
        var (owner, zone, _) = await SetupAsync(factory);
        var phone = NewPhone();
        var id = IdOf(await CreateAsync(owner, Profile($"Status {NewCode()}", phone, zone.Id)));
        var other = NewPhone();

        await EndAsync(owner, id, new DateOnly(2026, 10, 21));
        await (await owner.PostAsJsonAsync($"/api/employees/{id}/resume", new { rowVersion = await CurrentVersionAsync(owner, id), phoneNumber = other })).BodyAsync(HttpStatusCode.OK);

        var rows = await factory.QueryAsync(db => db.AuditEvents.AsNoTracking().Where(e => e.RecordId == id).ToListAsync());
        var changes = rows.Where(e => e.Action == "Employee.StatusChanged").OrderBy(e => e.OccurredAt).ToList();
        Assert.Equal(2, changes.Count);
        Assert.Equal(("WORKING", "LEFT"), (StatusIn(changes[0].Before), StatusIn(changes[0].After)));
        Assert.Contains("2026-10-21", changes[0].Reason);
        Assert.Equal(("LEFT", "WORKING"), (StatusIn(changes[1].Before), StatusIn(changes[1].After)));
        Assert.Contains("2026-10-21", rows.Single(e => e.Action == "Employee.EmploymentEnded").After);
        var resumed = rows.Single(e => e.Action == "Employee.EmploymentResumed");
        Assert.Contains(phone, resumed.Before);
        Assert.Contains(other, resumed.After);
        Assert.All(rows, e => Assert.NotNull(e.ActorAccountId));
    }

    [Fact]
    public async Task Update_audits_only_changed_fields_with_masked_bank_numbers_and_logs_hold_no_personal_data()
    {
        var (owner, zone, _) = await SetupAsync(factory);
        var phone = NewPhone();
        var saved = await CreateAsync(owner, Profile($"Audit {NewCode()}", phone, zone.Id, b => b["bankAccount"] = Bank("999900000021")));
        var id = IdOf(saved);

        var updated = await owner.PutAsJsonAsync($"/api/employees/{id}", Profile(saved.GetProperty("employee").GetProperty("fullName").GetString()!, phone, zone.Id, b =>
        {
            b["bankAccount"] = Bank("999900000022");
            b["notes"] = "Chuyển sang ca đêm";
            b["rowVersion"] = RowVersionOf(saved);
        }));

        Assert.Equal(HttpStatusCode.OK, updated.StatusCode);
        var audit = await factory.QueryAsync(db => db.AuditEvents.AsNoTracking().SingleAsync(e => e.RecordId == id && e.Action == "Employee.Updated"));
        Assert.Equal(["BankAccount", "Notes"], JsonDocument.Parse(audit.After!).RootElement.EnumerateObject().Select(p => p.Name).Order());
        Assert.Contains("********0021", audit.Before);
        Assert.Contains("********0022", audit.After);
        var allAudit = await factory.QueryAsync(db => db.AuditEvents.Where(e => e.RecordId == id).Select(e => (e.Before ?? "") + (e.After ?? "")).ToListAsync());
        Assert.DoesNotContain(allAudit, a => a.Contains("999900000021") || a.Contains("999900000022"));
        Assert.DoesNotContain(factory.Logs.Lines, l => l.Contains("999900000021") || l.Contains("999900000022") || l.Contains(phone));
    }

    [Fact]
    public async Task List_pages_filters_by_status_and_phone_and_shows_the_linked_account_to_the_owner()
    {
        var (owner, zone, role) = await SetupAsync(factory);
        var tag = NewCode();
        var phone = NewPhone();
        var username = $"nv{NewCode()}";
        var a = IdOf(await CreateAsync(owner, Profile($"{tag} A", phone, zone.Id, b => b["account"] = AccountBlock(username, zone, role))));
        var b = IdOf(await CreateAsync(owner, Profile($"{tag} B", NewPhone(), zone.Id)));
        var c = IdOf(await CreateAsync(owner, Profile($"{tag} C", NewPhone(), (await factory.AddZoneAsync(NewCode())).Id)));
        await EndAsync(owner, b);

        var firstPage = await owner.GetFromJsonAsync<JsonElement>($"/api/employees?search={tag}&pageSize=2");
        var secondPage = await owner.GetFromJsonAsync<JsonElement>($"/api/employees?search={tag}&pageSize=2&page=2");
        var defaults = await owner.GetFromJsonAsync<JsonElement>($"/api/employees?search={tag}");
        var capped = await owner.GetFromJsonAsync<JsonElement>($"/api/employees?search={tag}&pageSize=500");
        var left = await owner.GetFromJsonAsync<JsonElement>($"/api/employees?search={tag}&statusId={StatusIds.Employee.Left}");
        var inZone = await owner.GetFromJsonAsync<JsonElement>($"/api/employees?search={tag}&zoneId={zone.Id}");
        var byPhone = await owner.GetFromJsonAsync<JsonElement>($"/api/employees?search={phone[3..]}");
        var asManager = await (await ManagerAsync(factory, zone)).GetFromJsonAsync<JsonElement>($"/api/employees?search={phone[3..]}");

        Assert.Equal([a, b], Ids(firstPage));
        Assert.Equal(3, firstPage.GetProperty("total").GetInt32());
        Assert.Equal([c], Ids(secondPage));
        Assert.Equal((1, 20), (defaults.GetProperty("page").GetInt32(), defaults.GetProperty("pageSize").GetInt32()));
        Assert.Equal(100, capped.GetProperty("pageSize").GetInt32());
        Assert.Equal([b], Ids(left));
        Assert.Equal("Left", left.GetProperty("items")[0].GetProperty("status").GetString());
        Assert.False(string.IsNullOrEmpty(left.GetProperty("items")[0].GetProperty("lastWorkingDay").GetString()));
        Assert.Equal([a, b], Ids(inZone));
        var row = byPhone.GetProperty("items").EnumerateArray().Single(i => i.GetProperty("id").GetGuid() == a);
        Assert.Equal((phone, username, "Active", "Employed"), (row.GetProperty("phoneNumber").GetString(),
            row.GetProperty("account").GetProperty("username").GetString(), row.GetProperty("account").GetProperty("status").GetString(), row.GetProperty("status").GetString()));
        Assert.Equal(zone.Code, row.GetProperty("workingZones")[0].GetProperty("code").GetString());
        Assert.Equal(JsonValueKind.Null, asManager.GetProperty("items").EnumerateArray().Single(i => i.GetProperty("id").GetGuid() == a).GetProperty("account").ValueKind);
    }

    private Task<int> StatusOfAsync(Guid id) => factory.QueryAsync(db => db.Employees.Where(e => e.Id == id).Select(e => e.StatusId).SingleAsync());

    private static string? StatusIn(string? json) => json is null ? null : JsonDocument.Parse(json).RootElement.GetProperty("Status").GetString();

    private static List<Guid> Ids(JsonElement page) => page.GetProperty("items").EnumerateArray().Select(i => i.GetProperty("id").GetGuid()).ToList();

    private sealed class FakeWorkRecords : IEmployeeWorkRecords
    {
        public DateOnly? LastAttendanceDay { get; set; }

        public List<(Guid EmployeeId, DateOnly Day)> RemovedAfter { get; } = [];

        public Task<DateOnly?> LastAttendanceDayAsync(Guid employeeId, CancellationToken cancellationToken) => Task.FromResult(LastAttendanceDay);

        public Task RemoveAssignmentsAfterAsync(Guid employeeId, DateOnly day, CancellationToken cancellationToken)
        {
            RemovedAfter.Add((employeeId, day));
            return Task.CompletedTask;
        }

        public Task<int> RemoveAssignmentsAfterLastWorkingDaysAsync(CancellationToken cancellationToken) => Task.FromResult(0);
    }

    private sealed class WorkRecordsApiFactory(IEmployeeWorkRecords records) : AuthApiFactory
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
