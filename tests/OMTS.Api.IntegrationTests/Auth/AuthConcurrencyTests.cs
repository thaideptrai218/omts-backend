using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using OMTS.Api.IntegrationTests.Infrastructure;

namespace OMTS.Api.IntegrationTests.Auth;

/// <summary>Races on one account or one session, against real SQL Server row versions.</summary>
public class AuthConcurrencyTests
{
    private const string Password = "Secret123";

    // Double click, or web and mobile at once: every login must answer normally, never 500.
    [SqlServerFact]
    public async Task Parallel_logins_of_one_account_all_succeed_or_fail_normally()
    {
        await using var database = SqlServerTestDatabase.Create();
        await MigrateAsync(database);
        await using var api = new SqlServerApiFactory(database);
        await api.AddAccountAsync("owner1", Password);
        var client = api.CreateClient();

        var right = await Task.WhenAll(Enumerable.Range(0, 4).Select(_ => PostLoginAsync(client, "owner1", Password)));
        var wrong = await Task.WhenAll(Enumerable.Range(0, 3).Select(_ => PostLoginAsync(client, "owner1", "Wrong123")));

        Assert.All(right, r => Assert.Equal(HttpStatusCode.OK, r.StatusCode));
        Assert.All(wrong, r => Assert.Equal(HttpStatusCode.Unauthorized, r.StatusCode));
    }

    // Two holders of one refresh token: at most one rotation succeeds and the session ends.
    [SqlServerFact]
    public async Task Parallel_refreshes_with_one_token_end_the_session()
    {
        await using var database = SqlServerTestDatabase.Create();
        await MigrateAsync(database);
        await using var api = new SqlServerApiFactory(database);
        await api.AddAccountAsync("owner2", Password);
        var client = api.CreateClient();
        var tokens = await (await PostLoginAsync(client, "owner2", Password)).Content.ReadFromJsonAsync<JsonElement>();
        var refreshToken = tokens.GetProperty("refreshToken").GetString();

        var responses = await Task.WhenAll(Enumerable.Range(0, 4)
            .Select(_ => client.PostAsJsonAsync("/api/auth/refresh", new { refreshToken })));

        var winners = responses.Where(r => r.StatusCode == HttpStatusCode.OK).ToList();
        Assert.True(winners.Count <= 1);
        Assert.All(responses.Except(winners), r => Assert.Equal(HttpStatusCode.Unauthorized, r.StatusCode));
        foreach (var winner in winners)
        {
            var rotated = (await winner.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("refreshToken").GetString();
            var afterReuse = await client.PostAsJsonAsync("/api/auth/refresh", new { refreshToken = rotated });
            Assert.Equal(HttpStatusCode.Unauthorized, afterReuse.StatusCode);
        }
    }

    // A browser or device name longer than the columns must not break login.
    [SqlServerFact]
    public async Task Overlong_user_agent_and_device_name_are_truncated()
    {
        await using var database = SqlServerTestDatabase.Create();
        await MigrateAsync(database);
        await using var api = new SqlServerApiFactory(database);
        await api.AddAccountAsync("owner3", Password);
        var client = api.CreateClient();
        client.DefaultRequestHeaders.UserAgent.ParseAdd(new string('a', 900));

        var response = await client.PostAsJsonAsync(
            "/api/auth/login", new { identifier = "owner3", password = Password, deviceName = new string('d', 400) });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    private static async Task MigrateAsync(SqlServerTestDatabase database)
    {
        await using var db = database.CreateContext();
        await db.Database.MigrateAsync();
    }

    private static Task<HttpResponseMessage> PostLoginAsync(HttpClient client, string identifier, string password) =>
        client.PostAsJsonAsync("/api/auth/login", new { identifier, password });
}
