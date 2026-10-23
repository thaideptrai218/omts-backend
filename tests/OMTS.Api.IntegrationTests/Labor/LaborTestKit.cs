using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using OMTS.Api.IntegrationTests.Infrastructure;
using OMTS.Application.Features.LaborCost;
using OMTS.Domain.Constants;
using OMTS.Domain.Entities.Farming;
using OMTS.Domain.Entities.Identity;
using OMTS.Domain.Entities.Labor;
using OMTS.Domain.Entities.Platform;
using OMTS.Domain.Enums;
using static OMTS.Api.IntegrationTests.Infrastructure.AcsTestKit;

namespace OMTS.Api.IntegrationTests.Labor;

/// <summary>A zone, its Owner (logged in) and the cooperative, for the LAB-02…LAB-10 tests.</summary>
public sealed record LaborWorld(AuthApiFactory Api, FarmingZone Zone, HttpClient Owner, Account OwnerAccount, Cooperative Cooperative)
{
    public string Assignments => $"/api/zones/{Zone.Id}/work-assignments";
    public string Attendance => $"/api/zones/{Zone.Id}/attendance";
}

/// <summary>
/// Set-up and request helpers. Test classes share one database per class, so each test takes its own payroll month
/// (<see cref="NewMonth"/>) and dates its attendance, piecework, rates and advances inside it.
/// </summary>
public static class LaborTestKit
{
    private static int _months;

    /// <summary>A month nobody else uses in this test run, going back from 09/2026 (all dates in it are in the past).</summary>
    public static PayrollPeriod NewMonth()
    {
        var n = Interlocked.Increment(ref _months) - 1;
        var start = new DateOnly(2026, 9, 1).AddMonths(-n);
        return new PayrollPeriod(start.Year, start.Month);
    }

    public static DateOnly Day(this PayrollPeriod period, int day) => new(period.Year, period.Month, day);

    public static async Task<LaborWorld> WorldAsync(this AuthApiFactory api)
    {
        var cooperative = await api.EnsureCooperativeAsync();
        var zone = await api.AddZoneAsync(NewCode());
        var owner = await api.AddAccountAsync(NewCode(), Password);
        return new LaborWorld(api, zone, await api.LoginAsync(owner.Username), owner, cooperative);
    }

    /// <summary>A staff account whose role has the given actions on each function, granted in <paramref name="zone"/> (All zones when null).</summary>
    public static async Task<(HttpClient Client, Account Account)> StaffAsync(
        this AuthApiFactory api, FarmingZone? zone, params (string Function, bool View, bool Create, bool Edit, bool Delete)[] permissions)
    {
        var role = new Role { Name = $"Labor {NewCode()}" };
        foreach (var (function, view, create, edit, delete) in permissions)
        {
            role.Permissions.Add(new RolePermission { FunctionCode = function, CanView = view, CanCreate = create, CanEdit = edit, CanDelete = delete });
        }

        await api.UseDbAsync(db => db.Roles.Add(role));
        var account = await api.AddAccountAsync(NewCode(), Password, SubjectType.Staff);
        await api.GrantAsync(account, role, zone);
        return (await api.LoginAsync(account.Username), account);
    }

    public static async Task<Employee> AddEmployeeAsync(this LaborWorld w, string? name = null, Action<Employee>? configure = null, bool inZone = true)
    {
        var employee = new Employee { CooperativeId = w.Cooperative.Id, FullName = name ?? $"NV {NewCode()}", PhoneNumber = NewPhone() };
        configure?.Invoke(employee);
        await w.Api.UseDbAsync(db =>
        {
            db.Employees.Add(employee);
            if (inZone)
            {
                db.EmployeeWorkingZones.Add(new EmployeeWorkingZone { EmployeeId = employee.Id, ZoneId = w.Zone.Id });
            }
        });
        return employee;
    }

    /// <summary>An employee linked to a new Active staff account; returns the client of that account.</summary>
    public static async Task<(Employee Employee, HttpClient Client, Account Account)> AddEmployeeWithAccountAsync(this LaborWorld w)
    {
        // Staff accounts need an effective grant to log in; a view right that opens nothing of LAB-10 is enough.
        var (client, account) = await w.Api.StaffAsync(w.Zone, (OMTS.Application.Features.Authorization.PermissionFunctions.Customers, true, false, false, false));
        var employee = await w.AddEmployeeAsync(configure: e => e.AccountId = account.Id);
        return (employee, client, account);
    }

    public static async Task<SeasonalWorker> AddWorkerAsync(this LaborWorld w, Guid? teamId = null, Action<SeasonalWorker>? configure = null)
    {
        var worker = new SeasonalWorker { FullName = $"TV {NewCode()}", PhoneNumber = NewPhone(), ContractorId = teamId };
        configure?.Invoke(worker);
        await w.Api.UseDbAsync(db => db.SeasonalWorkers.Add(worker));
        return worker;
    }

    public static async Task<Contractor> AddTeamAsync(this LaborWorld w, Action<Contractor>? configure = null)
    {
        var team = new Contractor { Name = $"Tổ {NewCode()}" };
        configure?.Invoke(team);
        await w.Api.UseDbAsync(db => db.Contractors.Add(team));
        return team;
    }

    /// <summary>A rate bounded to the month by default, so it never leaks into the payrolls of other tests.</summary>
    public static async Task<PayRuleVersion> AddRateAsync(
        this LaborWorld w, PayrollPeriod month, Guid? employeeId, Guid? workerId, SalaryType type, decimal amount,
        Action<PayRuleVersion>? configure = null)
    {
        var rate = new PayRuleVersion
        {
            EmployeeId = employeeId, WorkerId = workerId, SalaryType = type, Amount = amount, ValidFrom = month.Start, ValidTo = month.End,
            StandardWorkingDays = type == SalaryType.PerWorkingDay ? 26 : null, EnteredByAccountId = w.OwnerAccount.Id, StatusId = StatusIds.PayRule.Approved,
        };
        configure?.Invoke(rate);
        await w.Api.UseDbAsync(db => db.PayRuleVersions.Add(rate));
        return rate;
    }

    public static Task AddAttendanceAsync(this LaborWorld w, DateOnly date, Guid? employeeId, Guid? workerId, AttendanceResult result = AttendanceResult.Present) =>
        w.Api.UseDbAsync(db => db.AttendanceRecords.Add(new AttendanceRecord
        {
            ZoneId = w.Zone.Id, EmployeeId = employeeId, WorkerId = workerId, WorkDate = date, Result = result, RecordedByAccountId = w.OwnerAccount.Id,
        }));

    public static async Task<WorkAssignment> AddPieceworkAsync(
        this LaborWorld w, DateOnly date, Guid? employeeId, Guid? workerId, Guid? teamId, decimal? completed = null, decimal rate = 2_500)
    {
        var assignment = new WorkAssignment
        {
            ZoneId = w.Zone.Id, EmployeeId = employeeId, WorkerId = workerId, ContractorId = teamId, WorkDate = date, Task = "Vệ sinh dây",
            PaymentType = WorkPaymentType.Piecework, PieceUnit = "rope", PlannedQuantity = 40, PieceRate = rate,
        };
        if (completed is { } quantity)
        {
            (assignment.CompletedQuantity, assignment.CompletionDate, assignment.CompletionConfirmedAt) = (quantity, date, DateTimeOffset.UtcNow);
        }

        await w.Api.UseDbAsync(db => db.WorkAssignments.Add(assignment));
        return assignment;
    }

    /// <summary>Creates the payroll of the month (Owner) and returns its body; fails the test unless it was created.</summary>
    public static async Task<JsonElement> CreatePayrollAsync(this LaborWorld w, PayrollPeriod month, HttpClient? client = null)
    {
        var response = await (client ?? w.Owner).PostAsJsonAsync("/api/payrolls", new { period = month.Key });
        return await ExpectAsync(response, HttpStatusCode.Created);
    }

    /// <summary>A finalized month: one MonthlyFixed employee, payroll created and finalized by the Owner.</summary>
    public static Task<PayrollPeriod> FinalizedMonthAsync(this LaborWorld w) => w.FinalizeMonthAsync(NewMonth());

    /// <summary>Adds a MonthlyFixed employee to the month, creates its payroll and finalizes it (Owner).</summary>
    public static async Task<PayrollPeriod> FinalizeMonthAsync(this LaborWorld w, PayrollPeriod month)
    {
        var employee = await w.AddEmployeeAsync();
        await w.AddRateAsync(month, employee.Id, null, SalaryType.MonthlyFixed, 5_000_000);
        await w.CreatePayrollAsync(month);
        await ExpectAsync(await w.Owner.PostAsync($"/api/payrolls/{month.Key}/finalize", null), HttpStatusCode.OK);
        return month;
    }

    /// <summary>The payslip of a payee in a payroll body.</summary>
    public static JsonElement PayslipOf(this JsonElement payroll, Guid payeeId) =>
        payroll.GetProperty("payslips").EnumerateArray().Single(p => p.GetProperty("payee").GetProperty("id").GetGuid() == payeeId);

    public static async Task<JsonElement> ExpectAsync(HttpResponseMessage response, HttpStatusCode status)
    {
        await response.Content.LoadIntoBufferAsync();
        var text = await response.Content.ReadAsStringAsync();
        Assert.True(response.StatusCode == status, $"Expected {(int)status}, got {(int)response.StatusCode}: {text}");
        return text.Length == 0 ? default : JsonDocument.Parse(text).RootElement;
    }

    /// <summary>Asserts the status and the problem code, and returns the problem body.</summary>
    public static async Task<JsonElement> ProblemAsync(this HttpResponseMessage response, HttpStatusCode status, string code)
    {
        var body = await ExpectAsync(response, status);
        Assert.Equal(code, body.GetProperty("code").GetString());
        return body;
    }

    public static string? Field(this JsonElement problem) => problem.TryGetProperty("field", out var field) ? field.GetString() : null;

    public static Task<List<AuditEvent>> AuditOfAsync(this AuthApiFactory api, Guid recordId) =>
        api.QueryAsync(db => db.AuditEvents.AsNoTracking().Where(e => e.RecordId == recordId).OrderBy(e => e.OccurredAt).ToListAsync());
}
