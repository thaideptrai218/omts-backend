using System.Net.Http.Json;
using OMTS.Api.IntegrationTests.Infrastructure;
using static OMTS.Api.IntegrationTests.Employees.EmployeeRequests;
using static OMTS.Api.IntegrationTests.Infrastructure.AcsTestKit;
using static OMTS.Api.IntegrationTests.Infrastructure.FarmLabSqlServerKit;

namespace OMTS.Api.IntegrationTests.Cooperatives;

/// <summary>Regressions of the bugs QA found in the OMTS-199 review fixes.</summary>
public class ReviewFixQaRegressionTests
{
    // The in-memory provider gives every row an empty version, so "" matches it: this repro needs SQL Server.
    [SqlServerFact]
    public async Task Empty_row_version_on_reinstate_is_MSG01_not_a_stale_screen()
    {
        await using var database = SqlServerTestDatabase.Create();
        await using var api = await StartAsync(database);
        var (owner, _, cooperative) = await OwnerWithCooperativeAsync(api);
        var zone = await AddZoneAsync(api, cooperative.Id);
        var id = IdOf(await CreateAsync(owner, Profile($"NV {NewCode()}", NewPhone(), zone.Id)));

        var response = await owner.PostAsJsonAsync($"/api/employees/{id}/reinstate", new { rowVersion = "" });

        Assert.Equal("MSG01", await response.ErrorCodeAsync());
    }

    [SqlServerFact]
    public async Task Empty_row_version_on_end_employment_is_MSG01_not_a_stale_screen()
    {
        await using var database = SqlServerTestDatabase.Create();
        await using var api = await StartAsync(database);
        var (owner, _, cooperative) = await OwnerWithCooperativeAsync(api);
        var zone = await AddZoneAsync(api, cooperative.Id);
        var id = IdOf(await CreateAsync(owner, Profile($"NV {NewCode()}", NewPhone(), zone.Id)));
        var today = DateOnly.FromDateTime(DateTime.UtcNow.AddHours(7)).ToString("yyyy-MM-dd");

        var response = await owner.PostAsJsonAsync($"/api/employees/{id}/end-employment", new { endDate = today, rowVersion = "" });

        Assert.Equal("MSG01", await response.ErrorCodeAsync());
    }
}
