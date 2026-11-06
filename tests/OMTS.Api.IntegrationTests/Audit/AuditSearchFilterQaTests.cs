using System.Net;
using System.Text.Json;
using OMTS.Api.IntegrationTests.Infrastructure;
using static OMTS.Api.IntegrationTests.Audit.AuditTestKit;

namespace OMTS.Api.IntegrationTests.Audit;

/// <summary>QA of OMTS-216 PR 0.6: ACCOUNT-10 filters at their edges (blank and padded values, paging, the period).</summary>
public class AuditSearchFilterQaTests(AuthApiFactory factory) : IClassFixture<AuthApiFactory>
{
    [Fact]
    public async Task Blank_filters_are_no_filter_and_padded_values_match_as_trimmed()
    {
        var (_, client) = await factory.OwnerAsync();
        var tag = NewTag();
        var now = DateTimeOffset.UtcNow;
        await factory.SeedAsync(
            Event($"{tag}-1", now.AddMinutes(-2), $"{tag}.Created", $"{tag}Record"),
            Event($"{tag}-2", now.AddMinutes(-1), $"{tag}.Updated", $"{tag}Record"));

        Assert.Equal([$"{tag}-2", $"{tag}-1"], Codes(await client.SearchAsync($"recordCode={tag}&action=%20%20&recordType=&zoneId=")));
        Assert.Equal([$"{tag}-1"], Codes(await client.SearchAsync($"recordCode=%20{tag}%20&action=%20{tag}.Created%20")));
        Assert.Equal([$"{tag}-2", $"{tag}-1"], Codes(await client.SearchAsync($"recordCode={tag}&recordType=%09{tag}Record%20")));
    }

    [Fact]
    public async Task Negative_paging_values_are_brought_back_into_range()
    {
        var (_, client) = await factory.OwnerAsync();
        var tag = NewTag();
        await factory.SeedAsync(Enumerable.Range(1, 3).Select(i => Event($"{tag}-{i}", DateTimeOffset.UtcNow.AddMinutes(-i))).ToArray());

        var page = (await client.SearchAsync($"recordCode={tag}&page=-5&pageSize=-1")).GetProperty("page");

        Assert.Equal((1, 1, 3), (page.GetProperty("page").GetInt32(), page.GetProperty("pageSize").GetInt32(), page.GetProperty("total").GetInt32()));
        Assert.Equal($"{tag}-1", page.GetProperty("items")[0].GetProperty("recordCode").GetString());
    }

    // Only "from" entered: "to" is today by default, so a "from" more than 5 years back is the value to fix, and the
    // error must point at the field the Owner entered (MSG-GEN-01 "the fields to fix are pointed out").
    [Fact]
    public async Task A_from_date_more_than_5_years_back_without_a_to_date_points_at_the_from_date()
    {
        var (_, client) = await factory.OwnerAsync();

        var response = await client.GetAsync($"/api/audit-events?from={Date(Today.AddYears(-6))}");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("MSG-GEN-01", await response.ErrorCodeAsync());
        Assert.Equal("From date", JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement.GetProperty("field").GetString());
    }

    // The message names the period, and the period answered is the one searched, also with no match.
    [Fact]
    public async Task An_empty_result_keeps_the_period_searched()
    {
        var (_, client) = await factory.OwnerAsync();
        var (from, to) = (new DateOnly(2024, 2, 1), new DateOnly(2024, 2, 29));

        var result = await client.SearchAsync($"from={Date(from)}&to={Date(to)}&action={NewTag()}.None");

        Assert.Equal((Date(from), Date(to)), (result.GetProperty("from").GetString(), result.GetProperty("to").GetString()));
        Assert.Equal("MSG-GEN-10", result.GetProperty("messageCode").GetString());
    }
}
