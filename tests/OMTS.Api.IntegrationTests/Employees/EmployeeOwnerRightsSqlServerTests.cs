using System.Net.Http.Json;
using System.Net;
using Microsoft.EntityFrameworkCore;
using OMTS.Api.IntegrationTests.Infrastructure;
using static OMTS.Api.IntegrationTests.Employees.EmployeeRequests;
using static OMTS.Api.IntegrationTests.Infrastructure.AcsTestKit;
using static OMTS.Api.IntegrationTests.Infrastructure.FarmLabSqlServerKit;

namespace OMTS.Api.IntegrationTests.Employees;

/// <summary>LAB-01 uses the ACS rule for refusing Owner rights, so the audit row fits its column on SQL Server.</summary>
public class EmployeeOwnerRightsSqlServerTests
{
    [SqlServerFact]
    public async Task Owner_rights_requested_with_an_overlong_username_are_refused_and_audited()
    {
        await using var database = SqlServerTestDatabase.Create();
        await using var api = await StartAsync(database);
        var (owner, _, cooperative) = await OwnerWithCooperativeAsync(api);
        var zone = await AddZoneAsync(api, cooperative.Id);
        var username = new string('o', 101);

        var onCreate = await owner.PostAsJsonAsync("/api/employees", Profile($"NV {NewCode()}", NewPhone(), zone.Id, b => b["account"] = new
        {
            username,
            subjectType = "CooperativeOwner",
            grants = Array.Empty<object>(),
        }));
        var employeeId = IdOf(await CreateAsync(owner, Profile($"NV {NewCode()}", NewPhone(), zone.Id)));
        var onAccount = await owner.PostAsJsonAsync($"/api/employees/{employeeId}/account", new { username, subjectType = "CooperativeOwner", grants = Array.Empty<object>() });

        Assert.Equal(HttpStatusCode.Forbidden, onCreate.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, onAccount.StatusCode);
        var refusals = await QueryAsync(api, db => db.AuditEvents.Where(e => e.Action == "Account.OwnerRightsRefused").Select(e => e.RecordCode).ToListAsync());
        Assert.Equal([new string('o', 100), new string('o', 100)], refusals);
        Assert.Equal(1, await QueryAsync(api, db => db.Employees.CountAsync()));
    }
}
