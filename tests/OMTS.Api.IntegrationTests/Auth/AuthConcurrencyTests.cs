using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using OMTS.Api.IntegrationTests.Infrastructure;
using OMTS.Application.Common.Interfaces.Persistence;
using OMTS.Domain.Common;
using OMTS.Infrastructure.Messaging;
using OMTS.Infrastructure.Persistence;

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
        await database.MigrateToLatestAsync();
        await using var api = new SqlServerApiFactory(database);
        await api.AddAccountAsync("owner1", Password);
        var client = api.CreateClient();

        var right = await Task.WhenAll(Enumerable.Range(0, 4).Select(_ => PostLoginAsync(client, "owner1", Password)));
        var wrong = await Task.WhenAll(Enumerable.Range(0, 3).Select(_ => PostLoginAsync(client, "owner1", "Wrong123")));

        Assert.All(right, r => Assert.Equal(HttpStatusCode.OK, r.StatusCode));
        Assert.All(wrong, r => Assert.Equal(HttpStatusCode.Unauthorized, r.StatusCode));
    }

    // Two holders of one refresh token (a double click, two tabs): the session survives, every request gets an access
    // token and exactly one of them rotates the refresh token. Whoever lost the race, or arrived after it, is answered
    // from the rotation that won: the replaced token, which stays valid repeatedly within its grace period.
    [SqlServerFact]
    public async Task Parallel_refreshes_with_one_token_keep_the_session_and_rotate_it_once()
    {
        await using var database = SqlServerTestDatabase.Create();
        await database.MigrateToLatestAsync();
        await using var api = new SqlServerApiFactory(database);
        await api.AddAccountAsync("owner2", Password);
        var client = api.CreateClient();
        var tokens = await (await PostLoginAsync(client, "owner2", Password)).Content.ReadFromJsonAsync<JsonElement>();
        var refreshToken = tokens.GetProperty("refreshToken").GetString();

        var responses = await Task.WhenAll(Enumerable.Range(0, 4)
            .Select(_ => client.PostAsJsonAsync("/api/auth/refresh", new { refreshToken })));

        Assert.All(responses, r => Assert.Equal(HttpStatusCode.OK, r.StatusCode));
        var answers = await Task.WhenAll(responses.Select(r => r.Content.ReadFromJsonAsync<JsonElement>()));
        var rotated = answers.Single(a => a.GetProperty("refreshTokenRotated").GetBoolean());
        Assert.All(answers.Where(a => !a.GetProperty("refreshTokenRotated").GetBoolean()),
            a => Assert.Equal(refreshToken, a.GetProperty("refreshToken").GetString()));
        var next = await client.PostAsJsonAsync("/api/auth/refresh", new { refreshToken = rotated.GetProperty("refreshToken").GetString() });
        Assert.Equal(HttpStatusCode.OK, next.StatusCode);
    }

    // The request that loses the save to another rotation is answered as in the grace period instead of ending the session.
    [SqlServerFact]
    public async Task Refresh_that_loses_the_rotation_race_is_answered_from_the_winning_rotation()
    {
        await using var database = SqlServerTestDatabase.Create();
        await database.MigrateToLatestAsync();
        await using var api = new SqlServerApiFactory(database);
        await api.AddAccountAsync("owner5", Password);
        var client = api.CreateClient();
        var login = await (await PostLoginAsync(client, "owner5", Password)).Content.ReadFromJsonAsync<JsonElement>();
        var refreshToken = login.GetProperty("refreshToken").GetString()!;
        var winner = new RotationRace(() => client.PostAsJsonAsync("/api/auth/refresh", new { refreshToken }));
        await using var racing = api.WithWebHostBuilder(builder => builder.ConfigureTestServices(services =>
        {
            services.RemoveAll<IUnitOfWork>();
            services.AddScoped<IUnitOfWork>(provider => new RotateBeforeSaveUnitOfWork(provider.GetRequiredService<OmtsDbContext>(), winner));
        }));

        var loser = await racing.CreateClient().PostAsJsonAsync("/api/auth/refresh", new { refreshToken });

        Assert.Equal(HttpStatusCode.OK, loser.StatusCode);
        var loserTokens = await loser.Content.ReadFromJsonAsync<JsonElement>();
        var winnerTokens = await (await winner.Response!).Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(winnerTokens.GetProperty("refreshTokenRotated").GetBoolean());
        Assert.False(loserTokens.GetProperty("refreshTokenRotated").GetBoolean());
        Assert.Equal(refreshToken, loserTokens.GetProperty("refreshToken").GetString());
        using var scope = api.Services.CreateScope();
        var stored = await scope.ServiceProvider.GetRequiredService<OmtsDbContext>().LoginSessions.AsNoTracking().SingleAsync();
        Assert.Null(stored.RevokedAt);
        var next = await client.PostAsJsonAsync("/api/auth/refresh", new { refreshToken = winnerTokens.GetProperty("refreshToken").GetString() });
        Assert.Equal(HttpStatusCode.OK, next.StatusCode);
    }

    // A browser or device name longer than the columns must not break login.
    [SqlServerFact]
    public async Task Overlong_user_agent_and_device_name_are_truncated()
    {
        await using var database = SqlServerTestDatabase.Create();
        await database.MigrateToLatestAsync();
        await using var api = new SqlServerApiFactory(database);
        await api.AddAccountAsync("owner3", Password);
        var client = api.CreateClient();
        client.DefaultRequestHeaders.UserAgent.ParseAdd(new string('a', 900));

        var response = await client.PostAsJsonAsync(
            "/api/auth/login", new { identifier = "owner3", password = Password, deviceName = new string('d', 400) });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    private static Task<HttpResponseMessage> PostLoginAsync(HttpClient client, string identifier, string password) =>
        client.PostAsJsonAsync("/api/auth/login", new { identifier, password });

    /// <summary>Another request refreshes the same token once, between the read of the request under test and its save.</summary>
    private sealed class RotationRace(Func<Task<HttpResponseMessage>> refresh)
    {
        public Task<HttpResponseMessage>? Response { get; private set; }

        public async Task RunAsync()
        {
            if (Response is not null)
            {
                return;
            }

            Response = refresh();
            await Response;
        }
    }

    private sealed class RotateBeforeSaveUnitOfWork(OmtsDbContext db, RotationRace race) : IUnitOfWork
    {
        private readonly UnitOfWork _inner = new(db, new PushOutbox());

        public async Task<int> SaveChangesAsync(CancellationToken cancellationToken = default)
        {
            await race.RunAsync();
            return await _inner.SaveChangesAsync(cancellationToken);
        }

        public Task ReloadAsync(object entity, CancellationToken cancellationToken = default) => _inner.ReloadAsync(entity, cancellationToken);

        public void DiscardChanges() => _inner.DiscardChanges();

        public void ExpectRowVersion(AuditableEntity entity, byte[] rowVersion) => _inner.ExpectRowVersion(entity, rowVersion);

        public void MarkChanged(AuditableEntity entity) => _inner.MarkChanged(entity);
    }
}
