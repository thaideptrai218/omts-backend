using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using OMTS.Api.IntegrationTests.Infrastructure;
using OMTS.Application.Features.Authorization;
using OMTS.Domain.Entities.Farming;
using OMTS.Domain.Entities.Identity;
using OMTS.Domain.Enums;
using OMTS.Infrastructure.Messaging.Fakes;
using OMTS.Infrastructure.Persistence;
using static OMTS.Api.IntegrationTests.Infrastructure.AcsTestKit;

namespace OMTS.Api.IntegrationTests.AccessManagement;

/// <summary>
/// ACS races on real SQL Server: unique indexes, rowversion and transactions decide the outcome, which the
/// in-memory provider cannot show. Every answer must be a normal 2xx/4xx, never a 500.
/// </summary>
public class AcsSqlServerTests
{
    private const string TraderPassword = "Trader2026";

    // Double click on "Create staff": one account is created, the other requests answer MSG-ACCOUNT-09 naming the username,
    // whether the check or the unique index stopped them.
    [SqlServerFact]
    public async Task Parallel_staff_creations_with_one_username_create_one_account()
    {
        await using var database = SqlServerTestDatabase.Create();
        await database.MigrateToLatestAsync();
        await using var api = new SqlServerApiFactory(database);
        var owner = await api.AddAccountAsync(NewCode(), Password);
        var client = await LoginAsync(api, owner.Username);
        var (zoneId, roleId) = await SeedZoneAndRoleAsync(database, api);
        var username = $"s{NewCode()}";

        var responses = await Task.WhenAll(Enumerable.Range(0, 4)
            .Select(_ => client.PostAsJsonAsync("/api/accounts", StaffBody(username, zoneId, roleId))));

        Assert.Single(responses, r => r.StatusCode == HttpStatusCode.Created);
        foreach (var refused in responses.Where(r => r.StatusCode != HttpStatusCode.Created))
        {
            Assert.Equal(HttpStatusCode.Conflict, refused.StatusCode);
            Assert.Equal("MSG-ACCOUNT-09", await refused.ErrorCodeAsync());
            Assert.Equal("Username", (await refused.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("field").GetString());
        }

        Assert.Equal(1, await ReadAsync(database, db => db.Accounts.CountAsync(a => a.Username == username)));
    }

    // The same owner saves a staff account from a stale version: refused with MSG06, also when two saves race.
    [SqlServerFact]
    public async Task Stale_staff_edit_is_refused_with_msg06_at_database_level()
    {
        await using var database = SqlServerTestDatabase.Create();
        await database.MigrateToLatestAsync();
        await using var api = new SqlServerApiFactory(database);
        var owner = await api.AddAccountAsync(NewCode(), Password);
        var client = await LoginAsync(api, owner.Username);
        var (zoneId, roleId) = await SeedZoneAndRoleAsync(database, api);
        var username = $"s{NewCode()}";
        var phone = NewPhone();
        var created = await client.PostAsJsonAsync("/api/accounts", StaffBody(username, zoneId, roleId, phone));
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var id = (await created.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("account").GetProperty("id").GetGuid();

        var original = await RowVersionAsync(client, id);
        var saved = await client.PutAsJsonAsync($"/api/accounts/{id}", EditBody("First edit", username, phone, original));
        Assert.Equal(HttpStatusCode.OK, saved.StatusCode);
        var stale = await client.PutAsJsonAsync($"/api/accounts/{id}", EditBody("Stale edit", username, phone, original));
        Assert.Equal("MSG06", await stale.ErrorCodeAsync());

        // Two saves from the same current version: the second one must fail on the row version in the database.
        var current = await RowVersionAsync(client, id);
        var racing = await Task.WhenAll(Enumerable.Range(0, 4)
            .Select(i => client.PutAsJsonAsync($"/api/accounts/{id}", EditBody($"Race {i}", username, phone, current))));

        Assert.Single(racing, r => r.StatusCode == HttpStatusCode.OK);
        foreach (var lost in racing.Where(r => r.StatusCode != HttpStatusCode.OK))
        {
            Assert.Equal(HttpStatusCode.Conflict, lost.StatusCode);
            Assert.Equal("MSG06", await lost.ErrorCodeAsync());
        }
    }

    private static object StaffBody(string username, Guid zoneId, Guid roleId, string? phone = null) => new
    {
        displayName = "Nguyễn Văn B",
        username,
        phoneNumber = phone ?? NewPhone(),
        grants = new[] { new { zoneId, roleId } },
    };

    private static object EditBody(string displayName, string username, string phone, string rowVersion) => new
    {
        displayName,
        username,
        phoneNumber = phone,
        email = (string?)null,
        rowVersion,
    };

    private static async Task<string> RowVersionAsync(HttpClient client, Guid id)
    {
        var account = await client.GetFromJsonAsync<JsonElement>($"/api/accounts/{id}");
        return account.GetProperty("rowVersion").GetString()!;
    }

    private static async Task<HttpClient> LoginAsync(SqlServerApiFactory api, string username)
    {
        var client = api.CreateClient();
        var tokens = await LoginTokensAsync(client, username);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", tokens.GetProperty("accessToken").GetString());
        return client;
    }

    private static async Task<(Guid ZoneId, Guid RoleId)> SeedZoneAndRoleAsync(SqlServerTestDatabase database, SqlServerApiFactory api)
    {
        await using var db = database.CreateContext();
        var zone = new FarmingZone { Code = NewCode(), Name = "Zone", Cooperative = new Cooperative { FullName = "HTX Trung Nam" } };
        db.FarmingZones.Add(zone);
        await db.SaveChangesAsync();
        return (zone.Id, (await api.RoleAsync(StaffRole.ZoneManager)).Id);
    }

    private static async Task<T> ReadAsync<T>(SqlServerTestDatabase database, Func<OmtsDbContext, Task<T>> query)
    {
        await using var db = database.CreateContext();
        return await query(db);
    }



    [SqlServerFact]
    public async Task Parallel_staff_creations_do_not_log_the_duplicate_username()
    {
        await using var database = SqlServerTestDatabase.Create();
        await database.MigrateToLatestAsync();
        await using var api = new SqlServerApiFactory(database);
        var owner = await api.AddAccountAsync(NewCode(), Password);
        var client = await LoginAsync(api, owner.Username);
        var (zoneId, roleId) = await SeedZoneAndRoleAsync(database, api);
        var username = $"s{NewCode()}";

        var responses = await Task.WhenAll(Enumerable.Range(0, 4)
            .Select(_ => client.PostAsJsonAsync("/api/accounts", StaffBody(username, zoneId, roleId))));

        Assert.Single(responses, r => r.StatusCode == HttpStatusCode.Created);
        Assert.DoesNotContain(api.Logs.Lines, line => line.Contains(username));
    }
}
