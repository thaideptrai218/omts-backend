using System.Net;
using System.Net.Http.Json;
using OMTS.Api.IntegrationTests.Infrastructure;
using OMTS.Domain.Entities.Farming;
using OMTS.Domain.Entities.Identity;
using OMTS.Domain.Entities.Labor;
using OMTS.Domain.Enums;
using static OMTS.Api.IntegrationTests.Infrastructure.AcsTestKit;
using static OMTS.Api.IntegrationTests.Labor.LaborSettingsTestKit;
using static OMTS.Api.IntegrationTests.Labor.WorkAssignmentTestKit;

namespace OMTS.Api.IntegrationTests.Labor;

/// <summary>
/// §I.4.4 for LABOR-02: the Zone Manager of the raft's zone and the Cooperative Owner; any other zone, the Accountant and a caller who is not
/// logged in are refused on every route, and nothing is changed. A Zone Manager with a pair in each zone works in both.
/// </summary>
public class WorkAssignmentAccessTests(WorkAssignmentHost host) : IClassFixture<WorkAssignmentHost>
{
    private sealed record Scene(FarmingZone Zone, Raft Raft, JobType Job, Employee Employee, WorkAssignment Assignment);

    private async Task<Scene> SceneAsync()
    {
        var zone = await host.AddZoneAsync(NewCode());
        await OwnerAsync(host);
        var (raft, job, employee) = (await AddRaftAsync(host, zone), await AddJobTypeAsync(host), await AddEmployeeAsync(host, [zone]));
        return new Scene(zone, raft, job, employee, await StoreAssignmentAsync(host, employee, raft, job, Today().AddDays(1)));
    }

    private static async Task<List<string>> RefusalsAsync(HttpClient client, Scene s, HttpStatusCode status, string? code)
    {
        var date = Iso(Today().AddDays(2));
        var calls = new (string Name, Func<Task<HttpResponseMessage>> Call)[]
        {
            ("assign", () => client.AssignAsync(s.Raft, Today().AddDays(2), s.Job, [s.Employee])),
            ("edit", () => client.PutAsJsonAsync($"/api/work-assignments/{s.Assignment.Id}", new { jobTypeId = s.Job.Id, rowVersion = "" })),
            ("remove", () => client.DeleteAsync($"/api/work-assignments/{s.Assignment.Id}?rowVersion=")),
            ("change job", () => client.ChangeJobAsync(s.Raft, s.Job, [s.Employee])),
            ("day", () => client.GetAsync($"/api/work-days/{date}")),
            ("assignable", () => client.GetAsync($"/api/work-days/{date}/assignable-employees")),
        };
        var wrong = new List<string>();
        foreach (var (name, call) in calls)
        {
            var response = await call();
            // The day and the list are of the caller's own zones: another zone's caller sees them empty, only a caller with no zone is refused.
            if (response.StatusCode != status || (code is not null && await response.ErrorCodeAsync() != code))
            {
                wrong.Add($"{name}: {(int)response.StatusCode}");
            }
        }

        return wrong;
    }

    [Fact]
    public async Task The_accountant_is_refused_on_every_route()
    {
        var s = await SceneAsync();
        var accountant = await StaffInZoneAsync(host, StaffRole.Accountant, null);

        Assert.Empty(await RefusalsAsync(accountant, s, HttpStatusCode.Forbidden, "MSG-GEN-08"));
        Assert.Equal(s.Raft.Id, Assert.Single(await host.RowsOfAsync(s.Employee, Today().AddDays(1))).RaftId);
        Assert.Empty(await host.RowsOfAsync(s.Employee, Today().AddDays(2)));
    }

    [Fact]
    public async Task A_caller_who_is_not_logged_in_is_refused_on_every_route()
    {
        var s = await SceneAsync();

        Assert.Empty(await RefusalsAsync(host.CreateClient(), s, HttpStatusCode.Unauthorized, null));
        Assert.Empty(await host.RowsOfAsync(s.Employee, Today().AddDays(2)));
    }

    [Fact]
    public async Task A_zone_manager_of_another_zone_is_refused_on_the_routes_of_a_raft_and_an_assignment()
    {
        var s = await SceneAsync();
        var other = await StaffInZoneAsync(host, StaffRole.ZoneManager, await host.AddZoneAsync(NewCode()));

        var wrong = await RefusalsAsync(other, s, HttpStatusCode.Forbidden, "MSG-GEN-08");

        // The work day and the list of employees answer 200 with what that manager's own zones hold: nothing of this zone's crews.
        Assert.Equal(["day: 200", "assignable: 200"], wrong);
        var day = await other.GetFromJsonAsync<System.Text.Json.JsonElement>($"/api/work-days/{Iso(Today().AddDays(1))}");
        Assert.DoesNotContain(day.GetProperty("rafts").EnumerateArray(), r => r.GetProperty("id").GetGuid() == s.Raft.Id);
        Assert.Equal(s.Raft.Id, Assert.Single(await host.RowsOfAsync(s.Employee, Today().AddDays(1))).RaftId);
        Assert.Empty(await host.RowsOfAsync(s.Employee, Today().AddDays(2)));
    }

    [Fact]
    public async Task A_zone_manager_of_the_zone_and_the_owner_pass_the_zone_check_on_every_route()
    {
        var s = await SceneAsync();
        var manager = await StaffInZoneAsync(host, StaffRole.ZoneManager, s.Zone);
        var owner = await OwnerAsync(host);

        foreach (var client in new[] { manager, owner })
        {
            var assigned = await client.AssignAsync(s.Raft, Today().AddDays(3), s.Job, [s.Employee]);
            Assert.Equal(HttpStatusCode.Created, assigned.StatusCode);
            Assert.Equal(HttpStatusCode.OK, (await client.GetAsync($"/api/work-days/{Iso(Today().AddDays(3))}")).StatusCode);
            Assert.Equal(HttpStatusCode.OK, (await client.GetAsync($"/api/work-days/{Iso(Today().AddDays(3))}/assignable-employees?raftId={s.Raft.Id}")).StatusCode);
            var row = (await host.RowsOfAsync(s.Employee, Today().AddDays(3))).Single();
            var version = Uri.EscapeDataString(await host.IssueVersionAsync(row.Id));
            Assert.Equal(HttpStatusCode.NoContent, (await client.DeleteAsync($"/api/work-assignments/{row.Id}?rowVersion={version}")).StatusCode);
        }
    }

    [Fact]
    public async Task A_zone_manager_with_a_pair_in_each_zone_works_in_both_and_a_zone_without_a_pair_stays_closed()
    {
        var (zoneA, zoneB, zoneC) = (await host.AddZoneAsync(NewCode()), await host.AddZoneAsync(NewCode()), await host.AddZoneAsync(NewCode()));
        await OwnerAsync(host);
        var both = await StaffInTwoZonesAsync(zoneA, zoneB);
        var (raftA, raftB, raftC, job, employee) = (await AddRaftAsync(host, zoneA), await AddRaftAsync(host, zoneB), await AddRaftAsync(host, zoneC), await AddJobTypeAsync(host), await AddEmployeeAsync(host, [zoneA]));

        Assert.Equal(HttpStatusCode.Created, (await both.AssignAsync(raftA, Today(), job, [employee])).StatusCode);
        Assert.Equal(HttpStatusCode.Created, (await both.AssignAsync(raftB, Today(), job, [employee], moveConfirmed: true)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await both.AssignAsync(raftC, Today(), job, [employee], moveConfirmed: true)).StatusCode);
        Assert.Equal(raftB.Id, Assert.Single(await host.RowsOfAsync(employee, Today())).RaftId);
    }

    private async Task<HttpClient> StaffInTwoZonesAsync(FarmingZone first, FarmingZone second)
    {
        var password = NewPassword();
        var account = await host.AddAccountAsync(NewCode(), password, SubjectType.Staff);
        await host.GrantAsync(account, StaffRole.ZoneManager, first);
        await host.GrantAsync(account, StaffRole.ZoneManager, second);
        return await host.LoginAsync(account.Username, password);
    }
}
