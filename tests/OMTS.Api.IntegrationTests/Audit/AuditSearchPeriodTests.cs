using System.Net;
using System.Text.Json;
using OMTS.Api.IntegrationTests.Infrastructure;
using static OMTS.Api.IntegrationTests.Audit.AuditTestKit;

namespace OMTS.Api.IntegrationTests.Audit;

/// <summary>ACCOUNT-10 period: Vietnam dates with both ends included, the last 30 days by default, at most 5 years.</summary>
public class AuditSearchPeriodTests(AuthApiFactory factory) : IClassFixture<AuthApiFactory>
{
    [Fact]
    public async Task The_period_is_the_last_30_days_by_default()
    {
        var (_, client) = await factory.OwnerAsync();
        var tag = NewTag();
        var today = Today;
        await factory.SeedAsync(
            Event($"{tag}-TODAY", StartOf(today).AddMinutes(1)),
            Event($"{tag}-DAY30", StartOf(today.AddDays(-29))),
            Event($"{tag}-DAY31", StartOf(today.AddDays(-29)).AddSeconds(-1)));

        var result = await client.SearchAsync($"recordCode={tag}");

        Assert.Equal([$"{tag}-TODAY", $"{tag}-DAY30"], Codes(result));
        Assert.Equal(Date(today.AddDays(-29)), result.GetProperty("from").GetString());
        Assert.Equal(Date(today), result.GetProperty("to").GetString());
        Assert.Equal(3, Codes(await client.SearchAsync($"recordCode={tag}&from={Date(today.AddDays(-60))}")).Count);
    }

    // The ends are Vietnam days (UTC+7): 00:00 of "from" up to the last second of "to", whatever the server's time zone.
    [Fact]
    public async Task Both_ends_of_the_period_are_whole_vietnam_days()
    {
        var (_, client) = await factory.OwnerAsync();
        var tag = NewTag();
        var (from, to) = (new DateOnly(2025, 3, 10), new DateOnly(2025, 3, 12));
        await factory.SeedAsync(
            Event($"{tag}-BEFORE", StartOf(from).AddSeconds(-1)),
            Event($"{tag}-FIRST", StartOf(from)),
            Event($"{tag}-LAST", StartOf(to.AddDays(1)).AddSeconds(-1)),
            Event($"{tag}-AFTER", StartOf(to.AddDays(1))));

        var result = await client.SearchAsync($"recordCode={tag}&from={Date(from)}&to={Date(to)}");

        Assert.Equal([$"{tag}-LAST", $"{tag}-FIRST"], Codes(result));
        // Only "to": the 30 days up to it.
        Assert.Equal([$"{tag}-LAST", $"{tag}-FIRST", $"{tag}-BEFORE"], Codes(await client.SearchAsync($"recordCode={tag}&to={Date(to)}")));
    }

    [Fact]
    public async Task A_period_of_exactly_5_years_is_searched_and_a_longer_one_is_refused()
    {
        var (_, client) = await factory.OwnerAsync();
        var to = Today;

        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync($"/api/audit-events?from={Date(to.AddYears(-5))}&to={Date(to)}")).StatusCode);
        await AssertRefusedAsync(client, $"from={Date(to.AddYears(-5).AddDays(-1))}&to={Date(to)}", "To date");
    }

    [Fact]
    public async Task Bad_periods_are_refused_with_msg_gen_01()
    {
        var (_, client) = await factory.OwnerAsync();
        var today = Today;

        await AssertRefusedAsync(client, $"from={Date(today)}&to={Date(today.AddDays(-1))}", "From date");
        await AssertRefusedAsync(client, $"from={Date(today.AddDays(1))}", "From date");
        await AssertRefusedAsync(client, "from=1999-12-31&to=2000-01-31", "From date");
        await AssertRefusedAsync(client, $"to={Date(today.AddYears(1).AddDays(1))}", "To date");
        await AssertRefusedAsync(client, "from=0001-01-01", "From date");
        await AssertRefusedAsync(client, "from=31/10/2026", "from");
        await AssertRefusedAsync(client, "to=yesterday", "to");
    }

    private static async Task AssertRefusedAsync(HttpClient client, string query, string field)
    {
        var response = await client.GetAsync($"/api/audit-events?{query}");
        Assert.True(response.StatusCode == HttpStatusCode.BadRequest, $"{query}: {(int)response.StatusCode}");
        Assert.Equal("MSG-GEN-01", await response.ErrorCodeAsync());
        Assert.Equal(field, JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement.GetProperty("field").GetString());
    }
}
