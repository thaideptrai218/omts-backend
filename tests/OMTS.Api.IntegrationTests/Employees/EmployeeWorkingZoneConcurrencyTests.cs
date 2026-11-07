using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using OMTS.Api.IntegrationTests.Infrastructure;
using static OMTS.Api.IntegrationTests.Employees.EmployeeRequests;
using static OMTS.Api.IntegrationTests.Infrastructure.AcsTestKit;
using static OMTS.Api.IntegrationTests.Infrastructure.FarmLabSqlServerKit;

namespace OMTS.Api.IntegrationTests.Employees;

/// <summary>
/// LAB-01 "RowVersion – required on update": an edit that changes only the working zones is still an edit
/// of the profile, so a client holding the older version must get MSG06 instead of overwriting it.
/// </summary>
public class EmployeeWorkingZoneConcurrencyTests
{
    [SqlServerFact]
    public async Task Editing_only_working_zones_makes_older_versions_stale()
    {
        await using var database = SqlServerTestDatabase.Create();
        await using var api = await StartAsync(database);
        var (owner, _, cooperative) = await OwnerWithCooperativeAsync(api);
        var (first, second) = (await AddZoneAsync(api, cooperative.Id), await AddZoneAsync(api, cooperative.Id));
        var (name, phone) = ($"Lê Thị {NewCode()}", NewPhone());
        var saved = await CreateAsync(owner, Profile(name, phone, first.Id));
        var id = IdOf(saved);

        // Tab 1 moves the person to the second zone; tab 2 still holds the version it read before.
        var zonesOnly = await owner.PutAsJsonAsync($"/api/employees/{id}", Profile(name, phone, second.Id, b => b["rowVersion"] = RowVersionOf(saved)));
        var afterZones = await zonesOnly.Content.ReadFromJsonAsync<JsonElement>();
        var staleTab = await owner.PutAsJsonAsync($"/api/employees/{id}", Profile(name, phone, first.Id, b => { b["notes"] = "Tab 2"; b["rowVersion"] = RowVersionOf(saved); }));

        Assert.Equal(HttpStatusCode.OK, zonesOnly.StatusCode);
        Assert.NotEqual(RowVersionOf(saved), RowVersionOf(afterZones));
        Assert.Equal("MSG06", await staleTab.ErrorCodeAsync());
        Assert.Equal([second.Id], await QueryAsync(api, db => db.EmployeeWorkingZones.Where(w => w.EmployeeId == id).Select(w => w.ZoneId).ToListAsync()));
    }
}
