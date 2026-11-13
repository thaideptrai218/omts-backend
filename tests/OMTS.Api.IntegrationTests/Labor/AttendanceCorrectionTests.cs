using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using OMTS.Api.IntegrationTests.Infrastructure;
using OMTS.Domain.Constants;
using OMTS.Domain.Enums;
using static OMTS.Api.IntegrationTests.Employees.EmployeeRequests;
using static OMTS.Api.IntegrationTests.Infrastructure.AcsTestKit;
using static OMTS.Api.IntegrationTests.Labor.LaborSettingsTestKit;
using static OMTS.Api.IntegrationTests.Labor.WorkTestKit;

namespace OMTS.Api.IntegrationTests.Labor;

/// <summary>LABOR-04.4: the Cooperative Owner corrects the check-in, the check-out and the job periods of any day, with a reason; hours follow BR-LABOR-05.</summary>
public class AttendanceCorrectionTests
{
    private static readonly DateOnly PastDay = new(2026, 3, 2);

    private static string Url(Guid employee, DateOnly day) => $"{Workforce}/attendance/{employee}/{Day(day)}";

    private static object Body(string? checkIn, string? checkOut, object[]? periods, string? reason = "Quên chấm công", string? rowVersion = null, DateOnly? day = null) => new
    {
        checkInAt = checkIn is null ? null : Time(day ?? PastDay, checkIn),
        checkOutAt = checkOut is null ? null : Time(day ?? PastDay, checkOut),
        periods,
        reason,
        rowVersion,
    };

    private static object Period(Guid jobTypeId, Guid raftId, string? startsAt, DateOnly? day = null) =>
        new { jobTypeId, raftId, startsAt = startsAt is null ? null : Time(day ?? PastDay, startsAt) };

    private static async Task SetBreakAndRoundingAsync(HttpClient owner)
    {
        var current = await owner.GetFromJsonAsync<JsonElement>(AttendanceSettings);
        var response = await owner.PostAsJsonAsync(AttendanceSettings, new { breakStart = "11:30", breakEnd = "12:30", roundingUnit = "Minutes15", effectiveFrom = "2020-01-01", rowVersion = current.VersionOf() });
        await response.BodyAsync(HttpStatusCode.OK);
    }

    [Fact]
    public async Task The_owner_corrects_a_day_and_the_hours_follow_the_srs_example()
    {
        await using var factory = new AuthApiFactory();
        var s = await SetupWorkAsync(factory);
        await SetBreakAndRoundingAsync(s.Owner);
        var employee = await AddEmployeeAsync(s, "Hoa");

        var response = await s.Owner.PutAsJsonAsync(Url(employee, PastDay), Body("06:10", "16:55", [Period(s.Hanging, s.Raft.Id, null), Period(s.Harvest, s.Raft.Id, "09:40")]));
        var saved = await response.BodyAsync(HttpStatusCode.OK);

        Assert.Equal(("MSG-GEN-05", "Attendance saved."), (saved.Str("messageCode"), saved.Str("message")));
        var day = saved.GetProperty("day");
        Assert.True(day.GetProperty("complete").GetBoolean());
        Assert.Equal(("Correction", "Correction"), (day.Str("checkInMethod"), day.Str("checkOutMethod")));
        var jobs = day.GetProperty("jobs").EnumerateArray().ToDictionary(j => j.GetProperty("jobTypeId").GetGuid(), j => j.GetProperty("hours").GetDecimal());
        Assert.Equal((3.5m, 6.25m), (jobs[s.Hanging], jobs[s.Harvest]));
        Assert.Equal(2, day.GetProperty("periods").GetArrayLength());
        var audit = await factory.QueryAsync(db => db.AuditEvents.SingleAsync(e => e.Action == "Attendance.Corrected"));
        Assert.Equal("Quên chấm công", audit.Reason);
        Assert.Equal(2, await factory.QueryAsync(db => db.WorkAssignments.CountAsync(w => w.EmployeeId == employee && w.Method == AttendanceMethod.Correction)));
    }

    [Fact]
    public async Task A_second_correction_replaces_the_periods_and_keeps_the_audit_of_the_values_before()
    {
        await using var factory = new AuthApiFactory();
        var s = await SetupWorkAsync(factory);
        var employee = await AddEmployeeAsync(s);
        var first = await (await s.Owner.PutAsJsonAsync(Url(employee, PastDay), Body("06:00", "12:00", [Period(s.Hanging, s.Raft.Id, null), Period(s.Harvest, s.Raft2.Id, "09:00")]))).BodyAsync(HttpStatusCode.OK);

        var second = await s.Owner.PutAsJsonAsync(Url(employee, PastDay), Body("06:30", "12:00", [Period(s.Harvest, s.Raft.Id, null)], "Sửa lại", first.GetProperty("day").Str("rowVersion")));
        var day = (await second.BodyAsync(HttpStatusCode.OK)).GetProperty("day");

        Assert.Equal([s.Harvest], day.GetProperty("periods").EnumerateArray().Select(p => p.GetProperty("jobTypeId").GetGuid()));
        Assert.Equal(1, await factory.QueryAsync(db => db.WorkAssignments.CountAsync(w => w.EmployeeId == employee)));
        Assert.Equal(1, await factory.QueryAsync(db => db.Attendances.CountAsync(a => a.EmployeeId == employee)));
        var corrections = await factory.QueryAsync(db => db.AuditEvents.Where(e => e.Action == "Attendance.Corrected").OrderBy(e => e.OccurredAt).ToListAsync());
        Assert.Equal(["Quên chấm công", "Sửa lại"], corrections.Select(e => e.Reason));
        Assert.Contains("06:00", corrections[1].Before);
        Assert.Contains("06:30", corrections[1].After);
        var view = await s.Owner.GetFromJsonAsync<JsonElement>(Url(employee, PastDay));
        Assert.Equal(1, view.GetProperty("jobs").GetArrayLength());
    }

    [Fact]
    public async Task A_missing_check_out_is_entered_on_the_day_of_an_incomplete_record()
    {
        await using var factory = new AuthApiFactory();
        var s = await SetupWorkAsync(factory);
        var employee = await AddEmployeeAsync(s);
        await AddPastDayAsync(s, employee, PastDay, "06:00", null, (s.Harvest, s.Raft.Id, null));
        var before = await s.Owner.GetFromJsonAsync<JsonElement>(Url(employee, PastDay));
        Assert.False(before.GetProperty("complete").GetBoolean());
        Assert.Equal(0, before.GetProperty("jobs").GetArrayLength());

        var saved = await s.Owner.PutAsJsonAsync(Url(employee, PastDay), Body("06:00", "10:00", [Period(s.Harvest, s.Raft.Id, null)], "Thiếu giờ ra", before.Str("rowVersion")));

        var day = (await saved.BodyAsync(HttpStatusCode.OK)).GetProperty("day");
        Assert.True(day.GetProperty("complete").GetBoolean());
        Assert.Equal(4m, day.GetProperty("jobs")[0].GetProperty("hours").GetDecimal());
        // The check-in was not changed, so it keeps its method; only the check-out is a correction.
        Assert.Equal(("Manual", "Correction"), (day.Str("checkInMethod"), day.Str("checkOutMethod")));
    }

    [Fact]
    public async Task A_staff_employee_is_corrected_with_times_only()
    {
        await using var factory = new AuthApiFactory();
        var s = await SetupWorkAsync(factory);
        var staff = await AddEmployeeAsync(s, staff: true);

        var withPeriod = await s.Owner.PutAsJsonAsync(Url(staff, PastDay), Body("07:00", "16:00", [Period(s.Harvest, s.Raft.Id, null)]));
        Assert.Equal("Job periods", await FieldAsync(withPeriod));

        var ok = await s.Owner.PutAsJsonAsync(Url(staff, PastDay), Body("07:00", "16:00", []));
        var day = (await ok.BodyAsync(HttpStatusCode.OK)).GetProperty("day");
        Assert.True(day.GetProperty("complete").GetBoolean());
        Assert.Equal("Staff", day.Str("type"));
        Assert.Equal(0, day.GetProperty("jobs").GetArrayLength());
    }

    [Fact]
    public async Task Invalid_times_missing_reasons_and_job_periods_are_refused_with_the_fields_named()
    {
        await using var factory = new AuthApiFactory();
        var s = await SetupWorkAsync(factory);
        var employee = await AddEmployeeAsync(s);
        var first = new[] { Period(s.Harvest, s.Raft.Id, null) };

        async Task<string?> FieldOf(object body, DateOnly? day = null) => await FieldAsync(await s.Owner.PutAsJsonAsync(Url(employee, day ?? PastDay), body));

        Assert.Equal("Reason", await FieldOf(Body("06:00", "10:00", first, reason: " ")));
        Assert.Equal("Check-out", await FieldOf(Body("10:00", "06:00", first)));
        Assert.Equal("Check-in", await FieldOf(Body(null, null, first)));
        Assert.Equal("Job periods", await FieldOf(Body("06:00", "10:00", [])));
        Assert.Equal("Job periods", await FieldOf(Body("06:00", "10:00", [first[0], Period(s.Hanging, s.Raft.Id, "05:00")])));
        Assert.Equal("Job periods", await FieldOf(Body("06:00", "10:00", [first[0], Period(s.Hanging, s.Raft.Id, "11:00")])));
        Assert.Equal("Job periods", await FieldOf(Body("06:00", "10:00", [Period(s.Harvest, s.Raft.Id, "07:00")])));
        Assert.Equal("Job type", await FieldOf(Body("06:00", "10:00", [Period(Guid.NewGuid(), s.Raft.Id, null)])));
        Assert.Equal("Raft", await FieldOf(Body("06:00", "10:00", [Period(s.Harvest, Guid.NewGuid(), null)])));
        // A time that is not on the day, and a day after today.
        Assert.Equal("Check-in", await FieldOf(Body("06:00", "10:00", first, day: PastDay.AddDays(1))));
        var future = Today().AddDays(1);
        Assert.Equal("Date", await FieldOf(Body("06:00", "10:00", [Period(s.Harvest, s.Raft.Id, null, future)], day: future), future));
        Assert.False(await factory.QueryAsync(db => db.Attendances.AnyAsync()));
    }

    [Fact]
    public async Task A_discontinued_job_type_cannot_be_chosen_but_one_the_day_already_has_stays()
    {
        await using var factory = new AuthApiFactory();
        var s = await SetupWorkAsync(factory);
        var employee = await AddEmployeeAsync(s);
        await AddPastDayAsync(s, employee, PastDay, "06:00", "10:00", (s.Harvest, s.Raft.Id, null));
        await s.Factory.UpdateAsync<OMTS.Domain.Entities.Labor.JobType>(s.Harvest, j => j.StatusId = StatusIds.Usage.Discontinued);
        await s.Factory.UpdateAsync<OMTS.Domain.Entities.Labor.JobType>(s.Hanging, j => j.StatusId = StatusIds.Usage.Discontinued);
        var version = (await s.Owner.GetFromJsonAsync<JsonElement>(Url(employee, PastDay))).Str("rowVersion");

        var stays = await s.Owner.PutAsJsonAsync(Url(employee, PastDay), Body("06:00", "11:00", [Period(s.Harvest, s.Raft.Id, null)], "Giờ ra", version));
        await stays.BodyAsync(HttpStatusCode.OK);

        var refused = await s.Owner.PutAsJsonAsync(Url(employee, PastDay), Body("06:00", "11:00", [Period(s.Hanging, s.Raft.Id, null)], "Đổi việc", version));
        Assert.Equal(("MSG-GEN-09", "Job type"), (await refused.ErrorCodeAsync(), await FieldAsync(refused)));
    }

    [Fact]
    public async Task Only_the_owner_corrects_attendance_and_a_locked_pay_period_cannot_be_corrected()
    {
        await using var factory = new AuthApiFactory();
        var s = await SetupWorkAsync(factory);
        var employee = await AddEmployeeAsync(s);
        var body = Body("06:00", "10:00", [Period(s.Harvest, s.Raft.Id, null)]);

        Assert.Equal("MSG-GEN-08", await (await s.Manager.PutAsJsonAsync(Url(employee, PastDay), body)).ErrorCodeAsync());
        Assert.Equal("MSG-GEN-08", await (await s.Manager.GetAsync(Url(employee, PastDay))).ErrorCodeAsync());
        Assert.Equal(HttpStatusCode.Unauthorized, (await factory.CreateClient().PutAsJsonAsync(Url(employee, PastDay), body)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await s.Owner.PutAsJsonAsync(Url(Guid.NewGuid(), PastDay), body)).StatusCode);

        await factory.AddPayrollAsync("2026-03-01", "2026-03-31", StatusIds.Payroll.AwaitingFinalization);
        var locked = await s.Owner.PutAsJsonAsync(Url(employee, PastDay), body);

        Assert.Equal(("MSG-LABOR-04", HttpStatusCode.Conflict), (await locked.ErrorCodeAsync(), locked.StatusCode));
        Assert.False(await factory.QueryAsync(db => db.Attendances.AnyAsync(a => a.EmployeeId == employee)));
        // A day after that pay period can still be corrected.
        var later = await s.Owner.PutAsJsonAsync(Url(employee, new DateOnly(2026, 4, 2)), Body("06:00", "10:00", [Period(s.Harvest, s.Raft.Id, null, new DateOnly(2026, 4, 2))], day: new DateOnly(2026, 4, 2)));
        Assert.Equal(HttpStatusCode.OK, later.StatusCode);
    }
}
