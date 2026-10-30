using System.Net.Http.Json;
using System.Net;
using Microsoft.EntityFrameworkCore;
using OMTS.Api.IntegrationTests.Infrastructure;
using OMTS.Application.Features.Auth;

namespace OMTS.Api.IntegrationTests.Auth;

/// <summary>Regressions of the bugs QA found in the OMTS-208 review fixes.</summary>
public class ReviewFixQaRegressionTests
{
    private const string Password = "Secret123";

    // Was a bug: the doc says a new identifier is "simply not counted" when full, but MemoryCache evicts older entries
    // instead. Flooding with made-up names resets the lock for a counted identifier (enumeration oracle).
    [Fact]
    public void Flooding_with_made_up_identifiers_does_not_reset_a_counted_identifier()
    {
        using var counter = new UnknownLoginCounter(maxIdentifiers: 50);
        for (var i = 0; i < 4; i++)
        {
            counter.RegisterFailure("qa-victim");
        }

        for (var i = 0; i < 5_000; i++)
        {
            counter.RegisterFailure($"qa-flood-{i}");
        }

        Assert.Equal(5, counter.RegisterFailure("qa-victim"));
    }

    // Was a bug: RegisterFailure is a non-atomic read-then-set, so parallel failures for one identifier lose updates.
    [Fact]
    public async Task Parallel_failures_for_one_made_up_identifier_are_all_counted()
    {
        using var counter = new UnknownLoginCounter();

        await Task.WhenAll(Enumerable.Range(0, 200).Select(_ => Task.Run(() => counter.RegisterFailure("qa-race"))));

        Assert.Equal(201, counter.RegisterFailure("qa-race"));
    }

    // Was a bug: a known account's failed-login save conflicts under a burst; AuthService retries only
    // MaxSaveAttempts (5) times, then the ConcurrencyConflictException escapes (MSG06 409) and the failure is not counted.
    [SqlServerFact]
    public async Task Ten_parallel_wrong_passwords_answer_401_or_423_and_then_lock_the_account()
    {
        await using var database = SqlServerTestDatabase.Create();
        await using (var db = database.CreateContext())
        {
            await db.Database.MigrateAsync();
        }

        await using var api = new SqlServerApiFactory(database);
        await api.AddAccountAsync("qa-burst", Password);
        var client = api.CreateClient();

        var responses = await Task.WhenAll(Enumerable.Range(0, 10).Select(_ => client.PostAsJsonAsync(
            "/api/auth/login", new { identifier = "qa-burst", password = "Wrong123" })));

        Assert.Equal(Array.Empty<int>(), responses.Select(r => (int)r.StatusCode).Where(s => s is not (401 or 423)).ToArray());
        Assert.Equal(HttpStatusCode.Locked, (await client.PostAsJsonAsync(
            "/api/auth/login", new { identifier = "qa-burst", password = Password })).StatusCode);
    }
}
