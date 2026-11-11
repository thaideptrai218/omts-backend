using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using OMTS.Api.IntegrationTests.Infrastructure;
using OMTS.Domain.Constants;
using OMTS.Domain.Entities.Labor;
using OMTS.Domain.Enums;
using static OMTS.Api.IntegrationTests.Infrastructure.AcsTestKit;
using static OMTS.Api.IntegrationTests.Labor.WorkAssignmentTestKit;

namespace OMTS.Api.IntegrationTests.Labor;

/// <summary>LABOR-02.0 steps 5 and 6: the employees to choose from, those of the caller's zones first by job type, and the search over the cooperative.</summary>
public class AssignableEmployeesTests(WorkAssignmentHost host) : IClassFixture<WorkAssignmentHost>
{
    private static string Url(DateOnly date, string query = "") => $"/api/work-days/{Iso(date)}/assignable-employees{query}";

    private static List<Guid> IdsOf(JsonElement list) => list.GetProperty("items").EnumerateArray().Select(e => e.GetProperty("id").GetGuid()).ToList();

    [Fact]
    public async Task The_list_holds_the_employed_employees_of_the_callers_zones_and_those_with_the_job_type_come_first()
    {
        var (zone, zoneB) = (await host.AddZoneAsync(NewCode()), await host.AddZoneAsync(NewCode()));
        var manager = await StaffInZoneAsync(host, StaffRole.ZoneManager, zone);
        var job = await AddJobTypeAsync(host);
        var alpha = await AddEmployeeAsync(host, [zone], change: e => e.FullName = $"Alpha {NewCode()}");
        var zeta = await AddEmployeeAsync(host, [zone], change: e => { e.FullName = $"Zeta {NewCode()}"; e.JobTypes = [new EmployeeJobType { JobTypeId = job.Id }]; });
        var (away, left, none) = (await AddEmployeeAsync(host, [zoneB]), await AddEmployeeAsync(host, [zone], change: e => e.StatusId = StatusIds.Employee.Left), await AddEmployeeAsync(host, []));

        var byName = await manager.GetFromJsonAsync<JsonElement>(Url(Today()));
        var byJob = await manager.GetFromJsonAsync<JsonElement>(Url(Today(), $"?jobTypeId={job.Id}"));

        Assert.Equal([alpha.Id, zeta.Id], IdsOf(byName));
        Assert.Equal([zeta.Id, alpha.Id], IdsOf(byJob));
        Assert.Equal([true, false], byJob.GetProperty("items").EnumerateArray().Select(e => e.GetProperty("matchesJobType").GetBoolean()));
        Assert.All(byName.GetProperty("items").EnumerateArray(), e => Assert.False(e.GetProperty("matchesJobType").GetBoolean()));
        Assert.DoesNotContain(away.Id, IdsOf(byName));
        Assert.DoesNotContain(left.Id, IdsOf(byName));
        Assert.DoesNotContain(none.Id, IdsOf(byName));
        Assert.False(byName.GetProperty("truncated").GetBoolean());
    }

    [Fact]
    public async Task Each_employee_shows_the_type_the_phone_number_the_raft_of_the_assignment_and_whether_checked_in()
    {
        var zone = await host.AddZoneAsync(NewCode());
        var manager = await StaffInZoneAsync(host, StaffRole.ZoneManager, zone);
        var (raft, job) = (await AddRaftAsync(host, zone), await AddJobTypeAsync(host));
        var (assigned, free, staff) = (await AddEmployeeAsync(host, [zone], change: e => e.FullName = $"A {NewCode()}"), await AddEmployeeAsync(host, [zone], change: e => e.FullName = $"B {NewCode()}"), await AddEmployeeAsync(host, [zone], EmployeeType.Staff, e => e.FullName = $"C {NewCode()}"));
        var created = (await manager.AssignAsync(raft, Today(), job, [assigned]).BodyAsync(HttpStatusCode.Created)).First();
        host.Attendance.CheckIn(assigned.Id, Today(), DateTimeOffset.UtcNow.AddHours(-1));

        var list = await manager.GetFromJsonAsync<JsonElement>(Url(Today(), $"?raftId={raft.Id}"));

        var items = list.GetProperty("items").EnumerateArray().ToList();
        Assert.Equal([assigned.Id, free.Id, staff.Id], items.Select(e => e.GetProperty("id").GetGuid()));
        Assert.Equal((assigned.PhoneNumber, "SeasonalWorker", true, true), (items[0].GetProperty("phoneNumber").GetString(), items[0].GetProperty("type").GetString(), items[0].GetProperty("checkedIn").GetBoolean(), items[0].GetProperty("inYourZones").GetBoolean()));
        var assignment = items[0].GetProperty("assignment");
        Assert.Equal((created.GetProperty("id").GetGuid(), raft.Code, job.Name), (assignment.GetProperty("assignmentId").GetGuid(), assignment.GetProperty("raftCode").GetString(), assignment.GetProperty("jobTypeName").GetString()));
        Assert.Equal((JsonValueKind.Null, false), (items[1].GetProperty("assignment").ValueKind, items[1].GetProperty("checkedIn").GetBoolean()));
        Assert.Equal("Staff", items[2].GetProperty("type").GetString());
        var tomorrow = await manager.GetFromJsonAsync<JsonElement>(Url(Today().AddDays(1)));
        Assert.All(tomorrow.GetProperty("items").EnumerateArray(), e => Assert.Equal(JsonValueKind.Null, e.GetProperty("assignment").ValueKind));
    }

    // BR-LABOR-01: "finds other Employed employees of the cooperative by name or phone number".
    [Fact]
    public async Task A_search_looks_at_the_whole_cooperative_by_name_or_phone_number()
    {
        var (zone, zoneB) = (await host.AddZoneAsync(NewCode()), await host.AddZoneAsync(NewCode()));
        var manager = await StaffInZoneAsync(host, StaffRole.ZoneManager, zone);
        var name = $"Tìm {NewCode()}";
        var (away, left) = (await AddEmployeeAsync(host, [zoneB], change: e => e.FullName = name), await AddEmployeeAsync(host, [zoneB], change: e => { e.FullName = $"{name} cũ"; e.StatusId = StatusIds.Employee.Left; }));

        var byName = await manager.GetFromJsonAsync<JsonElement>(Url(Today(), $"?search={Uri.EscapeDataString(name)}"));
        var byPhone = await manager.GetFromJsonAsync<JsonElement>(Url(Today(), $"?search={away.PhoneNumber}"));

        Assert.Equal([away.Id], IdsOf(byName));
        Assert.False(byName.GetProperty("items")[0].GetProperty("inYourZones").GetBoolean());
        Assert.Contains(away.Id, IdsOf(byPhone));
        Assert.DoesNotContain(left.Id, IdsOf(byPhone));
        Assert.Empty(IdsOf(await manager.GetFromJsonAsync<JsonElement>(Url(Today(), $"?search={new string('x', 201)}"))));
    }

    [Fact]
    public async Task A_list_longer_than_200_is_cut_and_says_so()
    {
        var zone = await host.AddZoneAsync(NewCode());
        var manager = await StaffInZoneAsync(host, StaffRole.ZoneManager, zone);
        var cooperative = await host.EnsureCooperativeAsync();
        await host.UseDbAsync(db => db.Employees.AddRange(Enumerable.Range(0, 201).Select(i => new OMTS.Domain.Entities.Identity.Employee
        {
            CooperativeId = cooperative.Id,
            FullName = $"Đông {i:000}",
            WorkingZones = [new OMTS.Domain.Entities.Identity.EmployeeWorkingZone { ZoneId = zone.Id }],
            TypePeriods = [new EmployeeTypePeriod { Type = EmployeeType.SeasonalWorker, EffectiveFrom = new DateOnly(2020, 1, 1) }],
        })));

        var list = await manager.GetFromJsonAsync<JsonElement>(Url(Today()));

        Assert.Equal(200, list.GetProperty("items").GetArrayLength());
        Assert.True(list.GetProperty("truncated").GetBoolean());
    }

    [Fact]
    public async Task The_raft_must_exist_and_be_in_the_callers_zones_and_the_accountant_is_refused()
    {
        var (zone, zoneB) = (await host.AddZoneAsync(NewCode()), await host.AddZoneAsync(NewCode()));
        var manager = await StaffInZoneAsync(host, StaffRole.ZoneManager, zone);
        var away = await AddRaftAsync(host, zoneB);
        var accountant = await StaffInZoneAsync(host, StaffRole.Accountant, null);

        await (await manager.GetAsync(Url(Today(), $"?raftId={away.Id}"))).SrsProblemAsync(HttpStatusCode.Forbidden, "MSG-GEN-08", "MSG02");
        Assert.Equal(HttpStatusCode.NotFound, (await manager.GetAsync(Url(Today(), $"?raftId={Guid.NewGuid()}"))).StatusCode);
        await (await accountant.GetAsync(Url(Today()))).SrsProblemAsync(HttpStatusCode.Forbidden, "MSG-GEN-08", "MSG02");
        Assert.Equal(HttpStatusCode.Unauthorized, (await host.CreateClient().GetAsync(Url(Today()))).StatusCode);
    }
}
