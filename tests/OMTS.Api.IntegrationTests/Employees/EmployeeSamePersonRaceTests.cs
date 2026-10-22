using System.Net;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using OMTS.Api.IntegrationTests.Infrastructure;
using static OMTS.Api.IntegrationTests.Employees.EmployeeRequests;
using static OMTS.Api.IntegrationTests.Infrastructure.AcsTestKit;
using static OMTS.Api.IntegrationTests.Infrastructure.FarmLabSqlServerKit;

namespace OMTS.Api.IntegrationTests.Employees;

/// <summary>
/// LAB-01 "A person has exactly one profile": a double click on Save (or web and mobile at once) must not
/// create two profiles of the same full name + phone.
/// </summary>
public class EmployeeSamePersonRaceTests
{
    [SqlServerFact]
    public async Task Parallel_creates_of_the_same_person_leave_one_profile()
    {
        await using var database = SqlServerTestDatabase.Create();
        await using var api = await StartAsync(database);
        var (owner, _, cooperative) = await OwnerWithCooperativeAsync(api);
        var zone = await AddZoneAsync(api, cooperative.Id);
        var (name, phone) = ($"Hoàng Văn {NewCode()}", NewPhone());

        var responses = await Task.WhenAll(Enumerable.Range(0, 6).Select(_ => owner.PostAsJsonAsync("/api/employees", Profile(name, phone, zone.Id))));

        Assert.Equal(1, await QueryAsync(api, db => db.Employees.CountAsync(e => e.PhoneNumber == phone)));
        Assert.Single(responses, r => r.StatusCode == HttpStatusCode.Created);
        Assert.All(responses.Where(r => r.StatusCode != HttpStatusCode.Created), r => Assert.Equal(HttpStatusCode.Conflict, r.StatusCode));
    }
}
