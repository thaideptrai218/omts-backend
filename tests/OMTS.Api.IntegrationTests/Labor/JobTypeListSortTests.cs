using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using OMTS.Api.IntegrationTests.Infrastructure;
using static OMTS.Api.IntegrationTests.Infrastructure.AcsTestKit;
using static OMTS.Api.IntegrationTests.Labor.LaborSettingsTestKit;

namespace OMTS.Api.IntegrationTests.Labor;

/// <summary>HR-B06: the job type list sorted on the server by name, rate in force, the date it applies from and status, over the whole result before paging.</summary>
public class JobTypeListSortTests(AuthApiFactory factory) : IClassFixture<AuthApiFactory>
{
    private static string[] Names(JsonElement page) => page.GetProperty("items").EnumerateArray().Select(i => i.GetProperty("name").GetString()!).ToArray();

    private static async Task<JsonElement> ListAsync(HttpClient client, string query) =>
        await (await client.GetAsync($"{JobTypes}?{query}")).BodyAsync(HttpStatusCode.OK);

    /// <summary>A: 50000 from 2020-01-01 (and 90000 from 2099); B: 30000 from 2021-06-01; C: 40000 from 2019-03-01; D: only a rate of 2099 (none in force), discontinued.</summary>
    private async Task<(HttpClient Owner, string Tag)> SeedAsync()
    {
        var owner = await OwnerAsync(factory);
        var tag = NewCode();
        var a = await AddJobTypeAsync(owner, $"{tag} A", 50_000, "2020-01-01");
        Assert.Equal(HttpStatusCode.OK, (await owner.PostRateAsync(a, 90_000, "2099-01-01")).StatusCode);
        await AddJobTypeAsync(owner, $"{tag} B", 30_000, "2021-06-01");
        await AddJobTypeAsync(owner, $"{tag} C", 40_000, "2019-03-01");
        var d = await AddJobTypeAsync(owner, $"{tag} D", 10_000, "2099-01-01");
        Assert.Equal(HttpStatusCode.OK, (await owner.PostAsJsonAsync($"{JobTypes}/{d.IdOf()}/discontinue", new { rowVersion = d.VersionOf() })).StatusCode);
        return (owner, tag);
    }

    [Fact]
    public async Task Sorting_by_name_goes_both_ways_and_the_default_is_unchanged()
    {
        var (owner, tag) = await SeedAsync();
        string[] ascending = [$"{tag} A", $"{tag} B", $"{tag} C", $"{tag} D"];

        Assert.Equal(ascending, Names(await ListAsync(owner, $"search={tag}")));
        Assert.Equal(ascending, Names(await ListAsync(owner, $"search={tag}&sortBy=name")));
        Assert.Equal(ascending.Reverse(), Names(await ListAsync(owner, $"search={tag}&sortBy=name&sortDir=desc")));
    }

    [Fact]
    public async Task Sorting_by_rate_uses_the_rate_in_force_today_and_puts_a_job_type_without_one_first_when_ascending()
    {
        var (owner, tag) = await SeedAsync();

        Assert.Equal([$"{tag} D", $"{tag} B", $"{tag} C", $"{tag} A"], Names(await ListAsync(owner, $"search={tag}&sortBy=rate")));
        Assert.Equal([$"{tag} A", $"{tag} C", $"{tag} B", $"{tag} D"], Names(await ListAsync(owner, $"search={tag}&sortBy=rate&sortDir=desc")));
    }

    [Fact]
    public async Task Sorting_by_the_date_the_current_rate_applies_from_and_by_status()
    {
        var (owner, tag) = await SeedAsync();

        // C 2019-03-01, A 2020-01-01, B 2021-06-01; D has no rate in force.
        Assert.Equal([$"{tag} D", $"{tag} C", $"{tag} A", $"{tag} B"], Names(await ListAsync(owner, $"search={tag}&sortBy=effectiveFrom")));
        Assert.Equal([$"{tag} B", $"{tag} A", $"{tag} C", $"{tag} D"], Names(await ListAsync(owner, $"search={tag}&sortBy=effectiveFrom&sortDir=desc")));
        // In Use before Discontinued.
        Assert.Equal($"{tag} D", Names(await ListAsync(owner, $"search={tag}&sortBy=status"))[^1]);
        Assert.Equal($"{tag} D", Names(await ListAsync(owner, $"search={tag}&sortBy=status&sortDir=desc"))[0]);
    }

    [Fact]
    public async Task The_sort_runs_over_the_whole_result_so_pages_follow_each_other()
    {
        var (owner, tag) = await SeedAsync();

        var first = Names(await ListAsync(owner, $"search={tag}&sortBy=rate&sortDir=desc&pageSize=2&page=1"));
        var second = Names(await ListAsync(owner, $"search={tag}&sortBy=rate&sortDir=desc&pageSize=2&page=2"));

        Assert.Equal([$"{tag} A", $"{tag} C", $"{tag} B", $"{tag} D"], first.Concat(second));
    }

    [Theory]
    [InlineData("sortBy=salary", "Sort by")]
    [InlineData("sortBy=Rate", "Sort by")]
    [InlineData("sortDir=up", "Sort direction")]
    public async Task A_sort_value_that_does_not_exist_is_refused_with_400(string query, string field)
    {
        var owner = await OwnerAsync(factory);

        var problem = await (await owner.GetAsync($"{JobTypes}?{query}")).BodyAsync(HttpStatusCode.BadRequest);

        Assert.Equal("MSG-GEN-01", problem.GetProperty("code").GetString());
        Assert.Equal(field, problem.GetProperty("field").GetString());
    }
}
