using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using OMTS.Api.IntegrationTests.Employees;
using OMTS.Api.IntegrationTests.Infrastructure;
using OMTS.Domain.Entities.Farming;
using OMTS.Domain.Entities.Identity;
using OMTS.Domain.Entities.Labor;
using OMTS.Domain.Enums;
using static OMTS.Api.IntegrationTests.Employees.EmployeeRequests;
using static OMTS.Api.IntegrationTests.Infrastructure.AcsTestKit;
using static OMTS.Api.IntegrationTests.Labor.LaborSettingsTestKit;

namespace OMTS.Api.IntegrationTests.Labor;

/// <summary>
/// Set-up of the LABOR-02, LABOR-04, LABOR-05 and LABOR-10 tests: a zone with a Zone Manager and two rafts, another zone with a raft,
/// two job types, and employees made through the real API. Business days are computed when a test runs.
/// </summary>
internal sealed record WorkSetup(
    AuthApiFactory Factory, HttpClient Owner, HttpClient Manager, FarmingZone Zone, FarmingZone OtherZone, Raft Raft, Raft Raft2, Raft OtherRaft, Guid Harvest, Guid Hanging);

internal static class WorkTestKit
{
    public const string Workforce = "/api/workforce";
    public const string Me = "/api/me";

    public static async Task<WorkSetup> SetupWorkAsync(AuthApiFactory factory)
    {
        var owner = await OwnerAsync(factory);
        var zone = await factory.AddZoneAsync(NewCode());
        var other = await factory.AddZoneAsync(NewCode());
        var rafts = new[] { new Raft { ZoneId = zone.Id, Code = $"{zone.Code}-B01", Size = "1" }, new Raft { ZoneId = zone.Id, Code = $"{zone.Code}-B02", Size = "1" }, new Raft { ZoneId = other.Id, Code = $"{other.Code}-B01", Size = "1" } };
        await factory.UseDbAsync(db => db.Rafts.AddRange(rafts));
        var harvest = (await AddJobTypeAsync(owner, $"Harvest {NewCode()}")).IdOf();
        var hanging = (await AddJobTypeAsync(owner, $"Hanging {NewCode()}")).IdOf();
        return new WorkSetup(factory, owner, await ManagerAsync(factory, zone), zone, other, rafts[0], rafts[1], rafts[2], harvest, hanging);
    }

    /// <summary>An Employed employee of the zone, made through the API as the Owner; a Staff employee when <paramref name="staff"/>.</summary>
    public static async Task<Guid> AddEmployeeAsync(WorkSetup s, string? name = null, bool staff = false, FarmingZone? zone = null)
    {
        var profile = Profile(name ?? $"Worker {NewCode()}", NewPhone(), (zone ?? s.Zone).Id, b =>
        {
            if (staff)
            {
                b["employeeType"] = nameof(EmployeeType.Staff);
            }
        });
        return IdOf(await CreateAsync(s.Owner, profile));
    }

    /// <summary>The employee linked to a new staff account, whose client is returned: that is how an employee reads and sends their own work.</summary>
    public static async Task<(Guid EmployeeId, HttpClient Client)> AddLinkedEmployeeAsync(WorkSetup s, bool staff = false)
    {
        var employeeId = await AddEmployeeAsync(s, staff: staff);
        var account = await s.Factory.AddAccountAsync(NewCode(), Password, SubjectType.Staff);
        await s.Factory.GrantAsync(account, StaffRole.ZoneManager, s.Zone);
        await s.Factory.UpdateAsync<Employee>(employeeId, e => e.AccountId = account.Id);
        return (employeeId, await s.Factory.LoginAsync(account.Username));
    }

    public static string Day(DateOnly date) => date.ToString("yyyy-MM-dd");

    public static Task<HttpResponseMessage> AssignAsync(
        HttpClient client, Guid raftId, Guid jobTypeId, DateOnly date, bool moveExisting = false, params Guid[] employeeIds) =>
        client.PostAsJsonAsync($"{Workforce}/assignments", new { raftId, jobTypeId, date = Day(date), employeeIds, moveExisting });

    /// <summary>Assigns the employees today and returns the rows; fails the test when it is not 201.</summary>
    public static async Task<JsonElement> AssignTodayAsync(WorkSetup s, Raft raft, Guid jobTypeId, params Guid[] employeeIds) =>
        await (await AssignAsync(s.Manager, raft.Id, jobTypeId, Today(), false, employeeIds)).BodyAsync(HttpStatusCode.Created);

    public static Guid RowId(JsonElement rows, Guid employeeId) =>
        rows.EnumerateArray().Single(r => r.GetProperty("employeeId").GetGuid() == employeeId).GetProperty("id").GetGuid();

    public static async Task<JsonElement> RowOfAsync(HttpClient client, Guid employeeId, DateOnly? day = null) =>
        (await client.GetFromJsonAsync<JsonElement>($"{Workforce}/assignments?day={Day(day ?? Today())}"))
            .EnumerateArray().Single(r => r.GetProperty("employeeId").GetGuid() == employeeId).Clone();

    public static Task<HttpResponseMessage> CheckInAsync(HttpClient client, Guid assignmentId, string? qrCode = null) =>
        client.PostAsJsonAsync($"{Workforce}/assignments/{assignmentId}/check-in", new { qrCode });

    public static Task<HttpResponseMessage> CheckOutAsync(HttpClient client, Guid assignmentId, string? qrCode = null) =>
        client.PostAsJsonAsync($"{Workforce}/assignments/{assignmentId}/check-out", new { qrCode });

    /// <summary>A time of a business day as the API reads it.</summary>
    public static string Time(DateOnly day, string time) => $"{Day(day)}T{time}:00+07:00";

    /// <summary>A work assignment and an attendance record written straight to the database, for a day in the past (assignments cannot be made for one).</summary>
    public static async Task AddPastDayAsync(WorkSetup s, Guid employeeId, DateOnly day, string? checkIn, string? checkOut, params (Guid JobTypeId, Guid RaftId, string? StartsAt)[] periods)
    {
        var account = await s.Factory.QueryAsync(db => db.Accounts.Select(a => a.Id).FirstAsync());
        await s.Factory.UseDbAsync(db =>
        {
            foreach (var (jobTypeId, raftId, startsAt) in periods)
            {
                db.WorkAssignments.Add(new WorkAssignment
                {
                    EmployeeId = employeeId, Date = day, JobTypeId = jobTypeId, RaftId = raftId, AssignedByAccountId = account,
                    StartsAt = startsAt is null ? null : DateTimeOffset.Parse(Time(day, startsAt)),
                });
            }

            if (checkIn is not null || checkOut is not null)
            {
                db.Attendances.Add(new Attendance
                {
                    EmployeeId = employeeId, Date = day,
                    CheckInAt = checkIn is null ? null : DateTimeOffset.Parse(Time(day, checkIn)), CheckInMethod = checkIn is null ? null : AttendanceMethod.Manual,
                    CheckOutAt = checkOut is null ? null : DateTimeOffset.Parse(Time(day, checkOut)), CheckOutMethod = checkOut is null ? null : AttendanceMethod.Manual,
                });
            }
        });
    }

    public static Task<int> AuditAsync(this AuthApiFactory factory, string action) => factory.AuditCountAsync(action);

    /// <summary>A payroll holding every day from a year ago to a year ahead, in the given status.</summary>
    public static Task<Payroll> LockEverythingAsync(this AuthApiFactory factory, int statusId) =>
        factory.AddPayrollAsync(Day(Today().AddYears(-1)), Day(Today().AddYears(1)), statusId);
}
