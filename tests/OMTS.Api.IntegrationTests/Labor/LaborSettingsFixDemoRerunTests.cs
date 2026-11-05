using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using OMTS.Api.IntegrationTests.Infrastructure;
using OMTS.Domain.Enums;
using OMTS.Infrastructure.Bootstrap;
using static OMTS.Api.IntegrationTests.Labor.LaborSettingsQaDemoTests;
using static OMTS.Api.IntegrationTests.Labor.LaborSettingsQaKit;
using static OMTS.Api.IntegrationTests.Labor.LaborSettingsTestKit;

namespace OMTS.Api.IntegrationTests.Labor;

/// <summary>QA M3 of OMTS-227: what people do with the demo job types stops no rerun of the seed, and a rerun adds nothing.</summary>
public class LaborSettingsFixDemoRerunTests
{
    // Real use of every demo job type, and a rerun on it: nothing added, nothing moved, no job type added back under its old name.
    [SqlServerFact]
    public async Task A_rerun_after_renames_and_a_discontinue_adds_and_moves_nothing()
    {
        await using var host = new Host();
        Assert.Equal(DeploymentBootstrapper.Success, (await host.RunAsync()).ExitCode);
        var (accountant, _) = await LoginNewAsync(host.App, StaffRole.Accountant);
        var jobTypes = await accountant.GetFromJsonAsync<JsonElement>(JobTypes);
        var items = jobTypes.GetProperty("items").EnumerateArray().ToList();
        Assert.Equal(4, items.Count);
        foreach (var jobType in items)
        {
            await (await accountant.PutAsJsonAsync($"{JobTypes}/{jobType.IdOf()}/name", new { name = $"Đã đổi {jobType.Str("name")}", rowVersion = jobType.VersionOf() })).BodyAsync(HttpStatusCode.OK);
        }

        var cleaning = await accountant.ReadJobTypeAsync(items.Single(j => j.Str("name") == "Vệ sinh dây giàn").IdOf());
        await (await accountant.PostAsJsonAsync($"{JobTypes}/{cleaning.IdOf()}/discontinue", new { rowVersion = cleaning.VersionOf() })).BodyAsync(HttpStatusCode.OK);
        var snapshot = await SnapshotAsync(host);

        Assert.Equal(DeploymentBootstrapper.Success, (await host.RunAsync()).ExitCode);

        Assert.Equal(snapshot, await SnapshotAsync(host));
    }

    // A demo employee a rerun adds again (a person changed the phone it is matched by) names the job types under their new names, and not a Discontinued one, which would stop the seed.
    [SqlServerFact]
    public async Task A_demo_employee_added_again_gets_the_renamed_job_types_and_no_discontinued_one()
    {
        await using var host = new Host();
        Assert.Equal(DeploymentBootstrapper.Success, (await host.RunAsync()).ExitCode);
        var (accountant, _) = await LoginNewAsync(host.App, StaffRole.Accountant);
        var items = (await accountant.GetFromJsonAsync<JsonElement>(JobTypes)).GetProperty("items").EnumerateArray().ToList();
        var harvesting = items.Single(j => j.Str("name") == "Thu hoạch");
        var hanging = items.Single(j => j.Str("name") == "Treo giống");
        await (await accountant.PutAsJsonAsync($"{JobTypes}/{harvesting.IdOf()}/name", new { name = "Thu hoạch hàu", rowVersion = harvesting.VersionOf() })).BodyAsync(HttpStatusCode.OK);
        await (await accountant.PostAsJsonAsync($"{JobTypes}/{hanging.IdOf()}/discontinue", new { rowVersion = hanging.VersionOf() })).BodyAsync(HttpStatusCode.OK);
        const string plannedPhone = "0900000017";
        var changedPhone = AcsTestKit.NewPhone();
        await host.QueryAsync(db => db.Employees.Where(e => e.PhoneNumber == plannedPhone).ExecuteUpdateAsync(s => s.SetProperty(e => e.PhoneNumber, changedPhone)));

        Assert.Equal(DeploymentBootstrapper.Success, (await host.RunAsync()).ExitCode);

        var added = await host.QueryAsync(db => db.EmployeeJobTypes.Where(l => l.Employee.PhoneNumber == plannedPhone).Select(l => l.JobTypeId).ToListAsync());
        Assert.Equal([harvesting.IdOf()], added);
        Assert.Equal(4, await host.QueryAsync(db => db.JobTypes.CountAsync()));
    }
}
