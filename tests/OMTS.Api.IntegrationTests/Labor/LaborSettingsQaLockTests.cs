using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using OMTS.Api.IntegrationTests.Employees;
using OMTS.Api.IntegrationTests.Infrastructure;
using OMTS.Domain.Constants;
using OMTS.Domain.Entities.Labor;
using OMTS.Domain.Enums;
using static OMTS.Api.IntegrationTests.Infrastructure.AcsTestKit;
using static OMTS.Api.IntegrationTests.Labor.LaborSettingsTestKit;

namespace OMTS.Api.IntegrationTests.Labor;

/// <summary>
/// QA of OMTS-227, BR-LABOR-13 and MSG-LABOR-04: every status against every dated write at the boundary days of a period that
/// crosses a month. Each test has its own host, as a payroll locks for the whole database.
/// </summary>
public class LaborSettingsQaLockTests
{
    private const string Start = "2020-09-26";
    private const string End = "2020-10-25";

    private static async Task<HttpResponseMessage> WriteRateAsync(HttpClient owner, string date)
    {
        // Its first rate is dated after every payroll of this class, as a rate of 2019 would be refused too.
        var jobType = await AddJobTypeAsync(owner, from: "2090-01-01");
        return await owner.PostRateAsync(jobType, 50_000, date);
    }

    private static Task<HttpResponseMessage> WriteNewJobTypeAsync(HttpClient owner, string date) =>
        owner.PostAsJsonAsync(JobTypes, new { name = $"Mới {NewCode()}", hourlyRate = 40_000, effectiveFrom = date });

    private static async Task<HttpResponseMessage> WriteAttendanceSettingsAsync(HttpClient owner, string date)
    {
        var version = (await owner.GetFromJsonAsync<JsonElement>(LaborSettingsTestKit.AttendanceSettings)).VersionOf();
        return await owner.PostAsJsonAsync(LaborSettingsTestKit.AttendanceSettings, new { roundingUnit = "Minutes15", effectiveFrom = date, rowVersion = version });
    }

    private static async Task<(string Name, string Phone, JsonElement Saved, Guid Zone)> AddEmployeeAsync(AuthApiFactory factory, HttpClient owner, string firstDate = "2019-01-01")
    {
        var zone = await factory.AddZoneAsync(NewCode());
        var (name, phone) = ($"Loại {NewCode()}", NewPhone());
        var saved = await EmployeeRequests.CreateAsync(owner, EmployeeRequests.Profile(name, phone, zone.Id, b => b["employeeTypeFrom"] = firstDate));
        return (name, phone, saved, zone.Id);
    }

    private static Task<HttpResponseMessage> PutTypeAsync(HttpClient client, (string Name, string Phone, JsonElement Saved, Guid Zone) employee, string type, string date) =>
        client.PutAsJsonAsync($"/api/employees/{EmployeeRequests.IdOf(employee.Saved)}", EmployeeRequests.Profile(employee.Name, employee.Phone, employee.Zone, b =>
        {
            b["employeeType"] = type;
            b["employeeTypeFrom"] = date;
            b["rowVersion"] = EmployeeRequests.RowVersionOf(employee.Saved);
        }));

    [Theory]
    [InlineData(StatusIds.Payroll.AwaitingFinalization, true)]
    [InlineData(StatusIds.Payroll.Finalized, true)]
    [InlineData(StatusIds.Payroll.Draft, false)]
    [InlineData(StatusIds.Payroll.SentBack, false)]
    public async Task Every_dated_write_at_every_boundary_day_follows_the_status_of_the_payroll(int statusId, bool locks)
    {
        await using var factory = new AuthApiFactory();
        var owner = await OwnerAsync(factory);
        var employeeAtEachDate = new Dictionary<string, (string, string, JsonElement, Guid)>();
        foreach (var date in new[] { "2020-09-25", Start, "2020-10-10", End, "2020-10-26" })
        {
            employeeAtEachDate[date] = await AddEmployeeAsync(factory, owner);
        }

        await factory.AddPayrollAsync(Start, End, statusId);

        foreach (var (date, employee) in employeeAtEachDate)
        {
            var isLocked = locks && date != "2020-10-26";
            foreach (var write in new (string What, Func<Task<HttpResponseMessage>> Call)[]
            {
                ("rate", () => WriteRateAsync(owner, date)),
                ("new job type", () => WriteNewJobTypeAsync(owner, date)),
                ("attendance settings", () => WriteAttendanceSettingsAsync(owner, date)),
                ("employee type", () => PutTypeAsync(owner, employee, nameof(EmployeeType.Staff), date)),
            })
            {
                var response = await write.Call();
                var text = await response.Content.ReadAsStringAsync();
                if (isLocked)
                {
                    var problem = await response.SrsProblemAsync(HttpStatusCode.Conflict, "MSG-LABOR-04", null);
                    Assert.Equal(End, problem.Str("lockedUntil"));
                    Assert.StartsWith("The payroll of 10/2020 is ", problem.Detail());
                    Assert.Contains($"cannot apply to {DateOnly.Parse(date):dd/MM/yyyy}", problem.Detail());
                }
                else
                {
                    Assert.True(response.IsSuccessStatusCode, $"{write.What} on {date}: {(int)response.StatusCode} {text}");
                }
            }
        }
    }

    [Fact]
    public async Task A_refused_write_saves_nothing_writes_no_audit_row_and_leaves_a_draft_untouched()
    {
        await using var factory = new AuthApiFactory();
        var owner = await OwnerAsync(factory);
        var jobType = await AddJobTypeAsync(owner, from: "2019-01-01");
        var employee = await AddEmployeeAsync(factory, owner);
        var draft = await factory.AddPayrollAsync("2020-09-01", "2020-09-30", StatusIds.Payroll.Draft);
        await factory.AddAwaitingPayrollAsync("2020-10-01", "2020-10-31");
        var audits = await factory.QueryAsync(db => db.AuditEvents.CountAsync());

        Assert.Equal("MSG-LABOR-04", await (await owner.PostRateAsync(jobType, 50_000, "2020-09-15")).ErrorCodeAsync());
        Assert.Equal("MSG-LABOR-04", await (await WriteAttendanceSettingsAsync(owner, "2020-09-15")).ErrorCodeAsync());
        Assert.Equal("MSG-LABOR-04", await (await PutTypeAsync(owner, employee, nameof(EmployeeType.Staff), "2020-09-15")).ErrorCodeAsync());

        Assert.Equal(audits, await factory.QueryAsync(db => db.AuditEvents.CountAsync()));
        Assert.Null(await factory.QueryAsync(db => db.Payrolls.Where(p => p.Id == draft.Id).Select(p => p.UpdatedAt).SingleAsync()));
        Assert.Single(RatesOf(await owner.ReadJobTypeAsync(jobType.IdOf())));
    }

    [Fact]
    public async Task A_date_between_a_finalized_payroll_and_a_later_locked_one_is_refused_and_names_the_later_one()
    {
        await using var factory = new AuthApiFactory();
        var owner = await OwnerAsync(factory);
        await factory.AddPayrollAsync("2020-10-01", "2020-10-31", StatusIds.Payroll.Finalized);
        await factory.AddAwaitingPayrollAsync("2020-12-01", "2020-12-31");

        var refused = await WriteRateAsync(owner, "2020-11-15");
        var saved = await WriteRateAsync(owner, "2021-01-01");

        var problem = await refused.SrsProblemAsync(HttpStatusCode.Conflict, "MSG-LABOR-04", null);
        Assert.Equal(
            "The payroll of 12/2020 is Awaiting finalization, so this change cannot apply to 15/11/2020. Choose a date after 31/12/2020, or ask the Cooperative Owner to send the payroll back first.",
            problem.Detail());
        Assert.Equal(HttpStatusCode.OK, saved.StatusCode);
    }

    [Fact]
    public async Task A_date_in_a_draft_after_a_finalized_payroll_is_saved_and_touches_only_the_draft()
    {
        await using var factory = new AuthApiFactory();
        var owner = await OwnerAsync(factory);
        var finalized = await factory.AddPayrollAsync("2020-10-01", "2020-10-31", StatusIds.Payroll.Finalized);
        var draft = await factory.AddPayrollAsync("2020-11-01", "2020-11-30", StatusIds.Payroll.Draft);

        Assert.Equal(HttpStatusCode.OK, (await WriteRateAsync(owner, "2020-11-01")).StatusCode);

        Assert.Null(await factory.QueryAsync(db => db.Payrolls.Where(p => p.Id == finalized.Id).Select(p => p.UpdatedAt).SingleAsync()));
        Assert.NotNull(await factory.QueryAsync(db => db.Payrolls.Where(p => p.Id == draft.Id).Select(p => p.UpdatedAt).SingleAsync()));
    }

    [Theory]
    [InlineData("2026-11-01", "2026-11-25", "2026-11-25", "2026-11-26", "11/2026")] // the shorter first period under a new start day
    [InlineData("2026-10-26", "2026-10-26", "2026-10-26", "2026-10-27", "10/2026")] // a period of one day
    [InlineData("2026-12-26", "2027-01-25", "2026-12-26", "2027-01-26", "01/2027")] // across a year
    [InlineData("2028-02-01", "2028-02-29", "2028-02-29", "2028-03-01", "02/2028")] // up to a leap day
    public async Task A_short_or_crossing_period_locks_exactly_its_days(string start, string end, string refused, string saved, string name)
    {
        await using var factory = new AuthApiFactory();
        var owner = await OwnerAsync(factory);
        await factory.AddPayrollAsync(start, end, StatusIds.Payroll.Finalized);

        var problem = await (await WriteRateAsync(owner, refused)).SrsProblemAsync(HttpStatusCode.Conflict, "MSG-LABOR-04", null);

        Assert.Equal(end, problem.Str("lockedUntil"));
        Assert.StartsWith($"The payroll of {name} is Finalized", problem.Detail());
        Assert.Equal(HttpStatusCode.OK, (await WriteRateAsync(owner, saved)).StatusCode);
    }

    // Nothing dated: a locked payroll stops none of these, and LABOR-16 keeps the stored periods (BR-LABOR-13).
    [Fact]
    public async Task What_is_not_dated_goes_on_while_a_payroll_is_locked()
    {
        await using var factory = new AuthApiFactory();
        var owner = await OwnerAsync(factory);
        var jobType = await AddJobTypeAsync(owner, from: "2020-10-10");
        var employee = await AddEmployeeAsync(factory, owner);
        await factory.AddPayrollAsync("2020-10-01", "2020-10-31", StatusIds.Payroll.Finalized);

        var renamed = (await (await owner.PutAsJsonAsync($"{JobTypes}/{jobType.IdOf()}/name", new { name = $"Đổi {NewCode()}", rowVersion = jobType.VersionOf() })).BodyAsync(HttpStatusCode.OK)).GetProperty("jobType");
        var stopped = (await (await owner.PostAsJsonAsync($"{JobTypes}/{jobType.IdOf()}/discontinue", new { rowVersion = renamed.VersionOf() })).BodyAsync(HttpStatusCode.OK)).GetProperty("jobType");
        await (await owner.PostAsJsonAsync($"{JobTypes}/{jobType.IdOf()}/resume", new { rowVersion = stopped.VersionOf() })).BodyAsync(HttpStatusCode.OK);
        var version = (await owner.GetFromJsonAsync<JsonElement>(PayrollSettings)).VersionOf();
        var settings = await (await owner.PutAsJsonAsync(PayrollSettings, new { payPeriodStartDay = 26, automaticPayroll = true, rowVersion = version })).BodyAsync(HttpStatusCode.OK);
        var edited = await owner.PutAsJsonAsync($"/api/employees/{EmployeeRequests.IdOf(employee.Saved)}", EmployeeRequests.Profile($"{employee.Name} sửa", employee.Phone, employee.Zone, b => b["rowVersion"] = EmployeeRequests.RowVersionOf(employee.Saved)));

        Assert.Equal(HttpStatusCode.OK, edited.StatusCode);
        Assert.Equal("2020-11-01", settings.GetProperty("settings").GetProperty("nextPeriods")[0].Str("start"));
        Assert.Equal("2020-11-25", settings.GetProperty("settings").GetProperty("nextPeriods")[0].Str("end"));
    }

    [Fact]
    public async Task The_lock_is_checked_for_a_zone_manager_too_and_before_the_phone_number()
    {
        await using var factory = new AuthApiFactory();
        var owner = await OwnerAsync(factory);
        var employee = await AddEmployeeAsync(factory, owner);
        var other = await AddEmployeeAsync(factory, owner);
        await factory.AddPayrollAsync("2020-10-01", "2020-10-31", StatusIds.Payroll.Finalized);
        var zone = await factory.QueryAsync(db => db.FarmingZones.SingleAsync(z => z.Id == employee.Zone));
        var (manager, _) = await EmployeesV2QaKit.StaffAsync(factory, (StaffRole.ZoneManager, zone));

        var byManager = await PutTypeAsync(manager, employee, nameof(EmployeeType.Staff), "2020-10-31");
        var withTakenPhone = await owner.PutAsJsonAsync($"/api/employees/{EmployeeRequests.IdOf(employee.Saved)}", EmployeeRequests.Profile(employee.Name, other.Phone, employee.Zone, b =>
        {
            b["employeeType"] = nameof(EmployeeType.Staff);
            b["employeeTypeFrom"] = "2020-10-31";
            b["rowVersion"] = EmployeeRequests.RowVersionOf(employee.Saved);
        }));

        Assert.Equal("MSG-LABOR-04", await byManager.ErrorCodeAsync());
        Assert.Equal("MSG-LABOR-04", await withTakenPhone.ErrorCodeAsync());
    }

    // The type of a day before every row is the first row's (BR-LABOR-01 as built, design Q11). A new earliest row after the
    // lock therefore moves the type of the locked days, though BR-LABOR-13 says a new type takes effect only after them.
    [Fact]
    public async Task M2_a_new_type_dated_before_the_first_one_changes_no_locked_day()
    {
        await using var factory = new AuthApiFactory();
        var owner = await OwnerAsync(factory);
        var employee = await AddEmployeeAsync(factory, owner, firstDate: "2021-01-01");
        var id = EmployeeRequests.IdOf(employee.Saved);
        var lockedDay = new DateOnly(2020, 10, 15);
        await factory.AddPayrollAsync("2020-10-01", "2020-10-31", StatusIds.Payroll.Finalized);
        Assert.Equal(EmployeeType.SeasonalWorker, await TypeOnAsync(factory, id, lockedDay));

        var response = await PutTypeAsync(owner, employee, nameof(EmployeeType.Staff), "2020-11-15");

        Assert.True(
            response.StatusCode == HttpStatusCode.Conflict || await TypeOnAsync(factory, id, lockedDay) == EmployeeType.SeasonalWorker,
            $"{(int)response.StatusCode}: the locked day {lockedDay} moved to {await TypeOnAsync(factory, id, lockedDay)}.");
    }

    [Fact]
    public async Task M2_replacing_a_first_type_dated_after_the_lock_changes_no_locked_day()
    {
        await using var factory = new AuthApiFactory();
        var owner = await OwnerAsync(factory);
        var employee = await AddEmployeeAsync(factory, owner, firstDate: "2020-11-01");
        var id = EmployeeRequests.IdOf(employee.Saved);
        var lockedDay = new DateOnly(2020, 10, 15);
        await factory.AddPayrollAsync("2020-10-01", "2020-10-31", StatusIds.Payroll.Finalized);

        var response = await PutTypeAsync(owner, employee, nameof(EmployeeType.Staff), "2020-11-01");

        Assert.True(
            response.StatusCode == HttpStatusCode.Conflict || await TypeOnAsync(factory, id, lockedDay) == EmployeeType.SeasonalWorker,
            $"{(int)response.StatusCode}: the locked day {lockedDay} moved to {await TypeOnAsync(factory, id, lockedDay)}.");
    }

    private static async Task<EmployeeType?> TypeOnAsync(AuthApiFactory factory, Guid employeeId, DateOnly day) =>
        EmployeeTypePeriod.TypeOn(await factory.QueryAsync(db => db.EmployeeTypePeriods.AsNoTracking().Where(p => p.EmployeeId == employeeId).ToListAsync()), day);
}
