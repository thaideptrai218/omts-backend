using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using OMTS.Api.IntegrationTests.Infrastructure;
using OMTS.Application.Common.Interfaces.Messaging;
using OMTS.Application.Common.Interfaces.Persistence;
using OMTS.Domain.Constants;
using OMTS.Domain.Entities.Farming;
using OMTS.Domain.Entities.Identity;
using OMTS.Domain.Entities.Labor;
using OMTS.Domain.Enums;
using static OMTS.Api.IntegrationTests.Infrastructure.AcsTestKit;

namespace OMTS.Api.IntegrationTests.Labor;

/// <summary>Attendance as a test says it is: LABOR-04 does not exist yet, so the reader answers what the test put in.</summary>
public sealed class FakeAttendanceDayReader : IAttendanceDayReader
{
    private readonly Dictionary<(DateOnly Date, Guid EmployeeId), DayAttendance> _days = [];

    public void CheckIn(Guid employeeId, DateOnly date, DateTimeOffset at, DateTimeOffset? checkOut = null) =>
        _days[(date, employeeId)] = new DayAttendance(employeeId, at, checkOut);

    public Task<IReadOnlyDictionary<Guid, DayAttendance>> ReadAsync(DateOnly date, IReadOnlyCollection<Guid> employeeIds, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyDictionary<Guid, DayAttendance>>(
            employeeIds.Where(id => _days.ContainsKey((date, id))).ToDictionary(id => id, id => _days[(date, id)]));
}

/// <summary>The employee push as a test sees it: what was sent, and optionally a push that fails.</summary>
public sealed class RecordingEmployeeWorkNotifier : IEmployeeWorkNotifier
{
    public List<WorkAssignmentNotice> Notices { get; } = [];

    public bool Fails { get; set; }

    public Task NotifyAsync(IReadOnlyList<WorkAssignmentNotice> notices, CancellationToken cancellationToken)
    {
        if (Fails)
        {
            throw new InvalidOperationException("The push service is down.");
        }

        Notices.AddRange(notices);
        return Task.CompletedTask;
    }
}

/// <summary>The API with the attendance reader and the employee push of the test; each test has its own host.</summary>
public sealed class WorkAssignmentHost : AuthApiFactory
{
    public FakeAttendanceDayReader Attendance { get; } = new();

    public RecordingEmployeeWorkNotifier Pushes { get; } = new();

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);
        builder.ConfigureTestServices(services =>
        {
            services.RemoveAll<IAttendanceDayReader>();
            services.AddSingleton<IAttendanceDayReader>(Attendance);
            services.RemoveAll<IEmployeeWorkNotifier>();
            services.AddSingleton<IEmployeeWorkNotifier>(Pushes);
        });
    }
}

/// <summary>Set-up and calls of the LABOR-02 tests. Names and phone numbers are unique, so tests can share a host.</summary>
internal static class WorkAssignmentTestKit
{
    public static DateOnly Today() => OMTS.Application.Common.BusinessClock.ToBusinessDate(DateTimeOffset.UtcNow);

    public static string Iso(DateOnly date) => date.ToString("yyyy-MM-dd");

    /// <summary>A logged-in staff member with the fixed role in the zone, or in All zones when null.</summary>
    public static async Task<HttpClient> StaffInZoneAsync(AuthApiFactory factory, StaffRole role, FarmingZone? zone)
    {
        await factory.EnsureCooperativeAsync();
        var password = NewPassword();
        var account = await factory.AddAccountAsync(NewCode(), password, SubjectType.Staff);
        await factory.GrantAsync(account, role, zone);
        return await factory.LoginAsync(account.Username, password);
    }

    public static async Task<Raft> AddRaftAsync(AuthApiFactory factory, FarmingZone zone, int statusId = StatusIds.Usage.InUse)
    {
        var raft = new Raft { ZoneId = zone.Id, Code = $"B{NewCode()[1..6]}", StatusId = statusId };
        await factory.UseDbAsync(db => db.Rafts.Add(raft));
        return raft;
    }

    /// <summary>An Employed employee of the cooperative with a working zone in each zone, a first type from 2020 and a phone number of their own.</summary>
    public static async Task<Employee> AddEmployeeAsync(
        AuthApiFactory factory, FarmingZone[] zones, EmployeeType type = EmployeeType.SeasonalWorker, Action<Employee>? change = null)
    {
        var cooperative = await factory.EnsureCooperativeAsync();
        var employee = new Employee
        {
            CooperativeId = cooperative.Id,
            FullName = $"Nhân viên {NewCode()}",
            PhoneNumber = NewPhone(),
            WorkingZones = zones.Select(z => new EmployeeWorkingZone { ZoneId = z.Id }).ToList(),
            TypePeriods = [new EmployeeTypePeriod { Type = type, EffectiveFrom = new DateOnly(2020, 1, 1) }],
        };
        change?.Invoke(employee);
        await factory.UseDbAsync(db => db.Employees.Add(employee));
        return employee;
    }

    public static Task<JobType> AddJobTypeAsync(AuthApiFactory factory, int statusId = StatusIds.Usage.InUse) =>
        OMTS.Api.IntegrationTests.Employees.EmployeeRequests.AddJobTypeAsync(factory, j => j.StatusId = statusId);

    /// <summary>A work assignment stored directly, for a date or a start time the API would not let a test create.</summary>
    public static async Task<WorkAssignment> StoreAssignmentAsync(
        AuthApiFactory factory, Employee employee, Raft raft, JobType jobType, DateOnly date, DateTimeOffset? startTime = null)
    {
        var assignment = new WorkAssignment
        {
            EmployeeId = employee.Id, RaftId = raft.Id, JobTypeId = jobType.Id, WorkDate = date, StartTime = startTime,
            AssignedByAccountId = (await factory.QueryAsync(db => db.Accounts.Select(a => a.Id).FirstAsync())),
        };
        await factory.UseDbAsync(db => db.WorkAssignments.Add(assignment));
        return assignment;
    }

    /// <summary>
    /// A row version the client can send back: the in-memory provider issues none (empty), which the guard answers as an invalid
    /// version, so a test gives the row one before it edits or removes it (as FarmingTestKit.IssueRowVersionAsync does).
    /// </summary>
    public static async Task<string> IssueVersionAsync(this AuthApiFactory factory, Guid assignmentId)
    {
        byte[] version = [0, 0, 0, 0, 0, 0, 7, (byte)Random.Shared.Next(1, 255)];
        await factory.UpdateAsync<WorkAssignment>(assignmentId, a => a.RowVersion = version);
        return Convert.ToBase64String(version);
    }

    public static Task<HttpResponseMessage> AssignAsync(
        this HttpClient client, Raft raft, DateOnly date, JobType jobType, Employee[] employees, bool moveConfirmed = false) =>
        client.PostAsJsonAsync($"/api/rafts/{raft.Id}/work-assignments", new
        {
            date = Iso(date), jobTypeId = jobType.Id, employeeIds = employees.Select(e => e.Id).ToArray(), moveConfirmed,
        });

    public static Task<HttpResponseMessage> ChangeJobAsync(
        this HttpClient client, Raft raft, JobType jobType, Employee[] employees, Raft? newRaft = null) =>
        client.PostAsJsonAsync($"/api/rafts/{raft.Id}/crew-job-changes", new { jobTypeId = jobType.Id, newRaftId = newRaft?.Id, employeeIds = employees.Select(e => e.Id).ToArray() });

    public static Task<WorkAssignment[]> RowsOfAsync(this AuthApiFactory factory, Employee employee, DateOnly date) =>
        factory.QueryAsync(async db => (await db.WorkAssignments.AsNoTracking().Where(a => a.EmployeeId == employee.Id && a.WorkDate == date).ToListAsync())
            .OrderBy(a => a.StartTime ?? DateTimeOffset.MinValue).ToArray());

    public static async Task<JsonElement> BodyAsync(this Task<HttpResponseMessage> response, System.Net.HttpStatusCode status) =>
        await (await response).BodyAsync(status);

    /// <summary>The first assignment of the response, as JSON.</summary>
    public static JsonElement First(this JsonElement saved) => saved.GetProperty("assignments")[0];

    public static string RowVersionOf(this JsonElement assignment) => assignment.GetProperty("rowVersion").GetString()!;
}
