using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using OMTS.Api.IntegrationTests.Infrastructure;
using OMTS.Domain.Constants;
using OMTS.Domain.Enums;
using static OMTS.Api.IntegrationTests.Employees.EmployeeRequests;
using static OMTS.Api.IntegrationTests.Infrastructure.AcsTestKit;
using static OMTS.Api.IntegrationTests.Labor.WorkTestKit;

namespace OMTS.Api.IntegrationTests.Labor;

/// <summary>LABOR-04 and BR-LABOR-06: check-in and check-out by QR code or by hand, Staff attendance, the crew start time and the pay period lock.</summary>
public class AttendanceRecordingTests(AuthApiFactory factory) : IClassFixture<AuthApiFactory>
{
    private async Task<(WorkSetup S, Guid Employee, Guid Row)> OneAssignedAsync(string? name = null)
    {
        var s = await SetupWorkAsync(factory);
        var employee = await AddEmployeeAsync(s, name);
        return (s, employee, RowId(await AssignTodayAsync(s, s.Raft, s.Harvest, employee), employee));
    }

    [Fact]
    public async Task A_check_in_and_a_check_out_by_hand_complete_the_day()
    {
        var (s, employee, row) = await OneAssignedAsync("Cường");

        var checkedIn = await (await CheckInAsync(s.Manager, row)).BodyAsync(HttpStatusCode.OK);

        Assert.Equal(row, checkedIn.GetProperty("id").GetGuid());
        Assert.NotNull(checkedIn.Str("checkedInAt"));
        Assert.Equal(JsonValueKind.Null, checkedIn.GetProperty("checkedOutAt").ValueKind);
        Assert.Equal("MSG-GEN-05", checkedIn.Str("messageCode"));
        Assert.Matches(@"^Check-in of Cường at \d\d:\d\d saved\.$", checkedIn.Str("message"));
        Assert.False(checkedIn.GetProperty("complete").GetBoolean());

        var checkedOut = await (await CheckOutAsync(s.Manager, row)).BodyAsync(HttpStatusCode.OK);

        Assert.NotNull(checkedOut.Str("checkedOutAt"));
        Assert.True(checkedOut.GetProperty("complete").GetBoolean());
        Assert.Equal("MSG-GEN-05", checkedOut.Str("messageCode"));
        var record = await factory.QueryAsync(db => db.Attendances.SingleAsync(a => a.EmployeeId == employee));
        Assert.Equal((AttendanceMethod.Manual, AttendanceMethod.Manual, Today()), (record.CheckInMethod, record.CheckOutMethod, record.Date));
        Assert.True(record.CheckOutAt > record.CheckInAt || record.CheckOutAt == record.CheckInAt);
        Assert.Equal(1, await factory.QueryAsync(db => db.AuditEvents.CountAsync(e => e.Action == "Attendance.CheckedIn" && e.RecordId == record.Id && e.ZoneId == s.Zone.Id)));
        Assert.Equal(1, await factory.QueryAsync(db => db.AuditEvents.CountAsync(e => e.Action == "Attendance.CheckedOut" && e.RecordId == record.Id)));
        var row2 = await RowOfAsync(s.Manager, employee);
        Assert.True(row2.GetProperty("complete").GetBoolean());
    }

    [Fact]
    public async Task A_second_check_in_or_check_out_is_refused_and_shows_the_time_already_recorded()
    {
        var (s, _, row) = await OneAssignedAsync("Dũng");
        var first = await (await CheckInAsync(s.Manager, row)).BodyAsync(HttpStatusCode.OK);

        var again = await CheckInAsync(s.Manager, row);

        Assert.Equal(HttpStatusCode.Conflict, again.StatusCode);
        var problem = await ProblemAsync(again);
        Assert.Equal("MSG-LABOR-14", problem.Str("code"));
        Assert.StartsWith("Dũng already has a check-in today, at ", problem.Str("detail"));
        await (await CheckOutAsync(s.Manager, row)).BodyAsync(HttpStatusCode.OK);
        Assert.Equal("MSG-LABOR-14", await (await CheckOutAsync(s.Manager, row)).ErrorCodeAsync());
        Assert.Equal(first.Str("checkedInAt"), (await RowOfAsync(s.Manager, (await factory.QueryAsync(db => db.WorkAssignments.SingleAsync(w => w.Id == row))).EmployeeId)).Str("checkedInAt"));
    }

    [Fact]
    public async Task A_check_out_without_a_check_in_is_saved_with_a_warning_and_the_day_stays_incomplete()
    {
        var (s, _, row) = await OneAssignedAsync("Giang");

        var checkedOut = await (await CheckOutAsync(s.Manager, row)).BodyAsync(HttpStatusCode.OK);

        Assert.Equal("MSG-LABOR-12", checkedOut.Str("messageCode"));
        Assert.NotNull(checkedOut.Str("checkedOutAt"));
        Assert.Equal(JsonValueKind.Null, checkedOut.GetProperty("checkedInAt").ValueKind);
        Assert.False(checkedOut.GetProperty("complete").GetBoolean());
    }

    [Fact]
    public async Task The_attendance_qr_code_of_the_employee_checks_them_in_and_another_code_does_not()
    {
        var s = await SetupWorkAsync(factory);
        var (mine, mineClient) = await AddLinkedEmployeeAsync(s);
        var other = await AddEmployeeAsync(s);
        var rows = await AssignTodayAsync(s, s.Raft, s.Harvest, mine, other);
        var qr = await mineClient.GetFromJsonAsync<JsonElement>($"{Me}/attendance-qr");
        Assert.Equal(60, qr.GetProperty("secondsValid").GetInt32());

        var wrong = await CheckInAsync(s.Manager, RowId(rows, other), qr.Str("code"));
        Assert.Equal(("MSG-LABOR-13", HttpStatusCode.BadRequest), (await wrong.ErrorCodeAsync(), wrong.StatusCode));
        Assert.Equal("MSG-LABOR-13", await (await CheckInAsync(s.Manager, RowId(rows, mine), "not a code")).ErrorCodeAsync());
        Assert.Equal("MSG-LABOR-13", await (await CheckInAsync(s.Manager, RowId(rows, mine), qr.Str("code") + "0")).ErrorCodeAsync());
        Assert.False(await factory.QueryAsync(db => db.Attendances.AnyAsync(a => a.EmployeeId == mine || a.EmployeeId == other)));

        await (await CheckInAsync(s.Manager, RowId(rows, mine), qr.Str("code"))).BodyAsync(HttpStatusCode.OK);

        Assert.Equal(AttendanceMethod.QrCode, (await factory.QueryAsync(db => db.Attendances.SingleAsync(a => a.EmployeeId == mine))).CheckInMethod);
    }

    [Fact]
    public async Task The_qr_code_needs_an_employee_profile_linked_to_the_account_and_a_working_employee()
    {
        var s = await SetupWorkAsync(factory);
        Assert.Equal("MSG-GEN-08", await (await s.Manager.GetAsync($"{Me}/attendance-qr")).ErrorCodeAsync());

        var (employee, client) = await AddLinkedEmployeeAsync(s);
        await EndAsync(s.Owner, employee);
        Assert.Equal("MSG-GEN-09", await (await client.GetAsync($"{Me}/attendance-qr")).ErrorCodeAsync());
    }

    [Fact]
    public async Task Nothing_is_recorded_for_a_left_employee_or_another_zone_or_a_role_without_attendance_rights()
    {
        var (s, employee, row) = await OneAssignedAsync();
        var otherManager = await ManagerAsync(factory, s.OtherZone);
        var accountant = await ManagerAsync(factory, null, StaffRole.Accountant);

        Assert.Equal("MSG-GEN-08", await (await CheckInAsync(otherManager, row)).ErrorCodeAsync());
        Assert.Equal("MSG-GEN-08", await (await CheckInAsync(accountant, row)).ErrorCodeAsync());
        Assert.Equal(HttpStatusCode.Unauthorized, (await CheckInAsync(factory.CreateClient(), row)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await CheckInAsync(s.Manager, Guid.NewGuid())).StatusCode);
        await s.Factory.UpdateAsync<OMTS.Domain.Entities.Identity.Employee>(employee, e => e.StatusId = StatusIds.Employee.Left);
        var left = await CheckInAsync(s.Manager, row);
        Assert.Equal(("MSG-GEN-09", "Employee"), (await left.ErrorCodeAsync(), await FieldAsync(left)));
        Assert.False(await factory.QueryAsync(db => db.Attendances.AnyAsync(a => a.EmployeeId == employee)));
    }

    [Fact]
    public async Task A_staff_employee_is_checked_in_from_the_staff_list_without_work_and_a_seasonal_worker_is_not()
    {
        var s = await SetupWorkAsync(factory);
        var staff = await AddEmployeeAsync(s, "Nhân viên A", staff: true);
        var seasonal = await AddEmployeeAsync(s, "Thời vụ B");
        var list = await s.Manager.GetFromJsonAsync<JsonElement>($"{Workforce}/staff-attendance");
        Assert.Contains(list.EnumerateArray(), e => e.GetProperty("employeeId").GetGuid() == staff);
        Assert.DoesNotContain(list.EnumerateArray(), e => e.GetProperty("employeeId").GetGuid() == seasonal);

        var checkedIn = await (await s.Manager.PostAsJsonAsync($"{Workforce}/staff/{staff}/check-in", new { qrCode = (string?)null })).BodyAsync(HttpStatusCode.OK);
        Assert.NotNull(checkedIn.Str("checkedInAt"));
        Assert.False(checkedIn.GetProperty("complete").GetBoolean());
        var checkedOut = await (await s.Manager.PostAsJsonAsync($"{Workforce}/staff/{staff}/check-out", new { qrCode = (string?)null })).BodyAsync(HttpStatusCode.OK);
        Assert.True(checkedOut.GetProperty("complete").GetBoolean());

        var refused = await s.Manager.PostAsJsonAsync($"{Workforce}/staff/{seasonal}/check-in", new { qrCode = (string?)null });
        Assert.Equal(("MSG-GEN-01", "Employee"), (await refused.ErrorCodeAsync(), await FieldAsync(refused)));
        var foreign = await AddEmployeeAsync(s, staff: true, zone: s.OtherZone);
        Assert.Equal("MSG-GEN-08", await (await s.Manager.PostAsJsonAsync($"{Workforce}/staff/{foreign}/check-in", new { qrCode = (string?)null })).ErrorCodeAsync());
        Assert.False(await factory.QueryAsync(db => db.WorkAssignments.AnyAsync(w => w.EmployeeId == staff)));
    }

    [Fact]
    public async Task The_start_time_of_a_crew_is_recorded_with_its_reason_and_checks_the_crew_in()
    {
        var s = await SetupWorkAsync(factory);
        var (a, b) = (await AddEmployeeAsync(s), await AddEmployeeAsync(s));
        await AssignTodayAsync(s, s.Raft, s.Harvest, a, b);

        var early = await s.Manager.PostAsJsonAsync($"{Workforce}/crew-start", new { raftId = s.Raft.Id, startTime = "00:00", reason = "Tôi đến muộn", employeeIds = new[] { a, b } });
        var rows = await early.BodyAsync(HttpStatusCode.OK);

        Assert.All(rows.EnumerateArray(), r => Assert.NotNull(r.Str("checkedInAt")));
        var records = await factory.QueryAsync(db => db.Attendances.Where(x => x.EmployeeId == a || x.EmployeeId == b).ToListAsync());
        Assert.All(records, r => Assert.Equal((AttendanceMethod.CrewStart, "Tôi đến muộn", Today()), (r.CheckInMethod, r.CheckInReason, BusinessDay(r.CheckInAt!.Value))));
        Assert.All(records, r => Assert.Equal(TimeSpan.Zero, r.CheckInAt!.Value.ToOffset(TimeSpan.FromHours(7)).TimeOfDay));

        var again = await s.Manager.PostAsJsonAsync($"{Workforce}/crew-start", new { raftId = s.Raft.Id, startTime = "00:00", reason = "Lại", employeeIds = new[] { a } });
        Assert.Equal("MSG-LABOR-14", await again.ErrorCodeAsync());
    }

    [Fact]
    public async Task A_crew_start_needs_a_reason_and_the_members_of_the_raft()
    {
        var s = await SetupWorkAsync(factory);
        var (a, b) = (await AddEmployeeAsync(s), await AddEmployeeAsync(s));
        await AssignTodayAsync(s, s.Raft, s.Harvest, a);
        await AssignTodayAsync(s, s.Raft2, s.Harvest, b);

        var noReason = await s.Manager.PostAsJsonAsync($"{Workforce}/crew-start", new { raftId = s.Raft.Id, startTime = "00:00", reason = " ", employeeIds = new[] { a } });
        Assert.Equal("Reason", await FieldAsync(noReason));
        var wrongRaft = await s.Manager.PostAsJsonAsync($"{Workforce}/crew-start", new { raftId = s.Raft.Id, startTime = "00:00", reason = "x", employeeIds = new[] { a, b } });
        Assert.Equal("Employees", await FieldAsync(wrongRaft));
        Assert.False(await factory.QueryAsync(db => db.Attendances.AnyAsync(x => x.EmployeeId == b)));
    }

    [Fact]
    public async Task Nothing_is_recorded_while_the_payroll_of_the_pay_period_is_awaiting_finalization_or_finalized()
    {
        foreach (var status in new[] { StatusIds.Payroll.AwaitingFinalization, StatusIds.Payroll.Finalized })
        {
            await using var own = new AuthApiFactory();
            var s = await SetupWorkAsync(own);
            var employee = await AddEmployeeAsync(s);
            var row = RowId(await AssignTodayAsync(s, s.Raft, s.Harvest, employee), employee);
            await own.LockEverythingAsync(status);

            var response = await CheckInAsync(s.Manager, row);

            Assert.Equal(("MSG-LABOR-04", HttpStatusCode.Conflict), (await response.ErrorCodeAsync(), response.StatusCode));
            Assert.False(await own.QueryAsync(db => db.Attendances.AnyAsync()));
            var assign = await AssignAsync(s.Manager, s.Raft2.Id, s.Harvest, Today(), true, employee);
            Assert.Equal("MSG-LABOR-04", await assign.ErrorCodeAsync());
        }
    }

    private static DateOnly BusinessDay(DateTimeOffset instant) => OMTS.Application.Common.BusinessClock.ToBusinessDate(instant);
}
