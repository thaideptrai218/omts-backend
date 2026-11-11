using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using OMTS.Api.IntegrationTests.Employees;
using OMTS.Api.IntegrationTests.Infrastructure;
using OMTS.Domain.Constants;
using OMTS.Domain.Entities.Farming;
using OMTS.Domain.Enums;
using static OMTS.Api.IntegrationTests.Employees.EmployeesV2QaKit;
using static OMTS.Api.IntegrationTests.Infrastructure.AcsTestKit;
using static OMTS.Api.IntegrationTests.Labor.LaborSettingsQaKit;
using static OMTS.Api.IntegrationTests.Labor.LaborSettingsTestKit;

namespace OMTS.Api.IntegrationTests.Labor;

/// <summary>
/// QA of OMTS-227, §I.4.4 and BR-FARM-08: revoked and mixed pairs on every route of LABOR-11, LABOR-15 and LABOR-16, and no rate
/// through any other route. The branch covers Owner, Accountant, Zone Manager, Trader and anonymous on the plain pairs.
/// </summary>
public class LaborSettingsQaAccessTests(AuthApiFactory factory) : IClassFixture<AuthApiFactory>
{
    private static async Task<HttpResponseMessage[]> EveryJobTypeRouteAsync(HttpClient client, Guid id)
    {
        var path = $"{JobTypes}/{id}";
        return
        [
            await client.GetAsync(JobTypes),
            await client.GetAsync(path),
            await client.PostAsJsonAsync(JobTypes, new { name = NewCode(), hourlyRate = 1, effectiveFrom = "2020-01-01" }),
            await client.PostAsJsonAsync($"{path}/rates", new { hourlyRate = 1, effectiveFrom = "2020-01-01", rowVersion = "" }),
            await client.PutAsJsonAsync($"{path}/name", new { name = NewCode(), rowVersion = "" }),
            await client.PostAsJsonAsync($"{path}/discontinue", new { rowVersion = "" }),
            await client.PostAsJsonAsync($"{path}/resume", new { rowVersion = "" }),
        ];
    }

    private static async Task<HttpResponseMessage[]> EverySettingsRouteAsync(HttpClient client) =>
    [
        await client.GetAsync(LaborSettingsTestKit.AttendanceSettings),
        await client.PostAsJsonAsync(LaborSettingsTestKit.AttendanceSettings, new { roundingUnit = "None", effectiveFrom = "2020-01-01", rowVersion = "" }),
        await client.GetAsync(PayrollSettings),
        await client.GetAsync($"{PayrollSettings}/preview?startDay=26"),
        await client.PutAsJsonAsync(PayrollSettings, new { payPeriodStartDay = 26, automaticPayroll = true, rowVersion = "" }),
    ];

    private static async Task AssertAllForbiddenAsync(IEnumerable<HttpResponseMessage> responses)
    {
        foreach (var response in responses)
        {
            await response.SrsProblemAsync(HttpStatusCode.Forbidden, "MSG-GEN-08", "MSG02");
        }
    }

    private async Task RevokeAsync(HttpClient owner, Guid accountId)
    {
        var grants = await factory.QueryAsync(db => db.AccessGrants.Where(g => g.AccountId == accountId && g.StatusId == StatusIds.AccessGrant.Effective).Select(g => g.Id).ToListAsync());
        foreach (var grant in grants)
        {
            await (await owner.PostAsJsonAsync($"/api/accounts/{accountId}/grants/{grant}/revoke", new { reason = "Thu hồi" })).JsonAsync(HttpStatusCode.OK);
        }
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task A_revoked_accountant_loses_every_route_from_the_next_action_and_a_new_pair_gives_them_back(bool allZones)
    {
        var owner = await OwnerAsync(factory);
        var jobType = await AddJobTypeAsync(owner);
        var zone = allZones ? null : await factory.AddZoneAsync(NewCode());
        var (accountant, account) = await StaffAsync(factory, (StaffRole.Accountant, zone));
        Assert.Equal(HttpStatusCode.OK, (await accountant.GetAsync(JobTypes)).StatusCode);

        await RevokeAsync(owner, account.Id);

        await AssertAllForbiddenAsync(await EveryJobTypeRouteAsync(accountant, jobType.IdOf()));
        await AssertAllForbiddenAsync(await EverySettingsRouteAsync(accountant));
        await factory.GrantAsync(account, StaffRole.Accountant, zone);
        Assert.Equal(HttpStatusCode.OK, (await accountant.GetAsync(JobTypes)).StatusCode);
    }

    [Fact]
    public async Task A_revoked_zone_manager_loses_the_options_from_the_next_action()
    {
        var owner = await OwnerAsync(factory);
        var (manager, account) = await StaffAsync(factory, (StaffRole.ZoneManager, await factory.AddZoneAsync(NewCode())));
        Assert.Equal(HttpStatusCode.OK, (await manager.GetAsync($"{JobTypes}/options")).StatusCode);

        await RevokeAsync(owner, account.Id);

        await AssertAllForbiddenAsync([await manager.GetAsync($"{JobTypes}/options")]);
    }

    // BR-FARM-08: a staff account may hold different roles in different zones; job types have no zone and follow the role only.
    [Fact]
    public async Task A_manager_who_is_also_an_accountant_of_one_zone_has_the_rates_and_the_options_and_no_settings()
    {
        var owner = await OwnerAsync(factory);
        var jobType = await AddJobTypeAsync(owner);
        var (both, _) = await StaffAsync(factory, (StaffRole.ZoneManager, await factory.AddZoneAsync(NewCode())), (StaffRole.Accountant, await factory.AddZoneAsync(NewCode())));

        Assert.Equal(HttpStatusCode.OK, (await both.GetAsync($"{JobTypes}/{jobType.IdOf()}")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await both.GetAsync($"{JobTypes}/options")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await both.PostRateAsync(jobType, 50_000, "2021-01-01")).StatusCode);
        await AssertAllForbiddenAsync(await EverySettingsRouteAsync(both));
    }

    [Fact]
    public async Task A_manager_who_keeps_the_manager_pair_after_the_accountant_pair_is_revoked_has_no_rates()
    {
        var owner = await OwnerAsync(factory);
        var jobType = await AddJobTypeAsync(owner);
        var accountantZone = await factory.AddZoneAsync(NewCode());
        var (client, account) = await StaffAsync(factory, (StaffRole.ZoneManager, await factory.AddZoneAsync(NewCode())), (StaffRole.Accountant, accountantZone));
        var pair = await factory.QueryAsync(db => db.AccessGrants.Where(g => g.AccountId == account.Id && g.ZoneId == accountantZone.Id).Select(g => g.Id).SingleAsync());

        await (await owner.PostAsJsonAsync($"/api/accounts/{account.Id}/grants/{pair}/revoke", new { reason = "Thu hồi" })).JsonAsync(HttpStatusCode.OK);

        await AssertAllForbiddenAsync(await EveryJobTypeRouteAsync(client, jobType.IdOf()));
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync($"{JobTypes}/options")).StatusCode);
    }

    // BR-FARM-08: the pairs of a discontinued zone stay Effective; records with no zone follow the role only.
    [Fact]
    public async Task An_accountant_whose_only_zone_is_discontinued_still_opens_job_types()
    {
        var owner = await OwnerAsync(factory);
        var jobType = await AddJobTypeAsync(owner);
        var zone = await factory.AddZoneAsync(NewCode());
        var (accountant, _) = await StaffAsync(factory, (StaffRole.Accountant, zone));

        await factory.UpdateAsync<FarmingZone>(zone.Id, z => z.StatusId = StatusIds.Usage.Discontinued);

        Assert.Equal(HttpStatusCode.OK, (await accountant.GetAsync($"{JobTypes}/{jobType.IdOf()}")).StatusCode);
    }

    [Fact]
    public async Task No_rate_reaches_a_zone_manager_through_the_options_or_the_employees_at_any_depth()
    {
        const string rate = "87654321";
        var owner = await OwnerAsync(factory);
        var name = $"Lương kín {NewCode()}";
        var jobType = await AddJobTypeAsync(owner, name, decimal.Parse(rate));
        var zone = await factory.AddZoneAsync(NewCode());
        var employeeName = $"Có việc {NewCode()}";
        var employee = await EmployeeRequests.CreateAsync(owner, EmployeeRequests.Profile(employeeName, NewPhone(), zone.Id, b => b["jobTypeIds"] = new[] { jobType.IdOf() }));
        var (manager, _) = await StaffAsync(factory, (StaffRole.ZoneManager, zone));

        var routes = new[] { $"{JobTypes}/options", "/api/employees", $"/api/employees?search={Uri.EscapeDataString(employeeName)}", $"/api/employees/{EmployeeRequests.IdOf(employee)}" };
        foreach (var route in routes)
        {
            var response = await manager.GetAsync(route);
            var text = await response.Content.ReadAsStringAsync();
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.DoesNotContain(rate, text);
            Assert.DoesNotContain(PropertyNames(JsonDocument.Parse(text).RootElement), p => p.Contains("rate", StringComparison.OrdinalIgnoreCase) || p.Contains("hourly", StringComparison.OrdinalIgnoreCase));
        }

        // ACCOUNT-10 is the Owner's: the audit rows hold the rates.
        await (await manager.GetAsync("/api/audit-events")).SrsProblemAsync(HttpStatusCode.Forbidden, "MSG-GEN-08", "MSG02");

        // The name itself does reach the manager, as the profile needs it.
        var detail = await manager.GetFromJsonAsync<JsonElement>($"/api/employees/{EmployeeRequests.IdOf(employee)}");
        Assert.Equal(["id", "name", "status"], detail.GetProperty("jobTypes")[0].EnumerateObject().Select(p => p.Name));
        Assert.Equal(name, detail.GetProperty("jobTypes")[0].Str("name"));
    }

    [Fact]
    public async Task The_options_follow_discontinue_and_resume_and_keep_the_order_of_the_names()
    {
        var owner = await OwnerAsync(factory);
        var tag = NewCode();
        var b = await AddJobTypeAsync(owner, $"{tag} B");
        var a = await AddJobTypeAsync(owner, $"{tag} A");
        var (manager, _) = await StaffAsync(factory, (StaffRole.ZoneManager, null));

        async Task<List<Guid>> OptionsAsync() => (await manager.GetFromJsonAsync<JsonElement>($"{JobTypes}/options")).EnumerateArray()
            .Where(o => o.Str("name")!.StartsWith(tag, StringComparison.Ordinal)).Select(o => o.IdOf()).ToList();

        Assert.Equal([a.IdOf(), b.IdOf()], await OptionsAsync());

        var stopped = (await (await owner.PostAsJsonAsync($"{JobTypes}/{a.IdOf()}/discontinue", new { rowVersion = a.VersionOf() })).BodyAsync(HttpStatusCode.OK)).GetProperty("jobType");
        Assert.Equal([b.IdOf()], await OptionsAsync());

        await (await owner.PostAsJsonAsync($"{JobTypes}/{a.IdOf()}/resume", new { rowVersion = stopped.VersionOf() })).BodyAsync(HttpStatusCode.OK);
        Assert.Equal([a.IdOf(), b.IdOf()], await OptionsAsync());
    }

    [Fact]
    public async Task An_accountant_of_one_zone_renames_discontinues_and_resumes_and_is_named_in_the_audit()
    {
        var zone = await factory.AddZoneAsync(NewCode());
        await factory.EnsureCooperativeAsync();
        var (accountant, account) = await StaffAsync(factory, (StaffRole.Accountant, zone));
        var jobType = await AddJobTypeAsync(accountant);
        var path = $"{JobTypes}/{jobType.IdOf()}";

        var renamed = (await (await accountant.PutAsJsonAsync($"{path}/name", new { name = $"Đổi {NewCode()}", rowVersion = jobType.VersionOf() })).BodyAsync(HttpStatusCode.OK)).GetProperty("jobType");
        var stopped = (await (await accountant.PostAsJsonAsync($"{path}/discontinue", new { rowVersion = renamed.VersionOf() })).BodyAsync(HttpStatusCode.OK)).GetProperty("jobType");
        await (await accountant.PostAsJsonAsync($"{path}/resume", new { rowVersion = stopped.VersionOf() })).BodyAsync(HttpStatusCode.OK);

        var actors = await factory.QueryAsync(db => db.AuditEvents.Where(e => e.RecordId == jobType.IdOf()).Select(e => e.ActorAccountId).ToListAsync());
        Assert.Equal(4, actors.Count); // created, renamed, discontinued, resumed
        Assert.All(actors, actor => Assert.Equal(account.Id, actor));
    }
}
