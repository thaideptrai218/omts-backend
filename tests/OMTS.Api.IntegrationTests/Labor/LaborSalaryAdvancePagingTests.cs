using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using OMTS.Api.IntegrationTests.Infrastructure;
using static OMTS.Api.IntegrationTests.Labor.LaborTestKit;

namespace OMTS.Api.IntegrationTests.Labor;

/// <summary>LAB-09 (review): GET /api/salary-advances is paged like the other lists; the per-person filter still works.</summary>
public class LaborSalaryAdvancePagingTests(AuthApiFactory factory) : IClassFixture<AuthApiFactory>
{
    private static async Task<Guid> AdvanceAsync(LaborWorld w, Guid workerId, int amount, DateOnly date) =>
        (await ExpectAsync(await w.Owner.PostAsJsonAsync("/api/salary-advances", new { workerId, amount, paymentDate = date, method = "Cash" }), HttpStatusCode.Created))
            .GetProperty("id").GetGuid();

    private static IEnumerable<Guid> Ids(JsonElement page) => page.GetProperty("items").EnumerateArray().Select(a => a.GetProperty("id").GetGuid());

    [Fact]
    public async Task Advances_without_a_person_come_in_pages_oldest_first_with_their_remaining_amounts()
    {
        await using var own = new AuthApiFactory(); // the total counts every advance in the database
        var w = await own.WorldAsync();
        var day = NewMonth().Day(2);
        var (first, second) = (await w.AddWorkerAsync(), await w.AddWorkerAsync());
        var a = await AdvanceAsync(w, first.Id, 100_000, day);
        var b = await AdvanceAsync(w, second.Id, 200_000, day.AddDays(1));
        var c = await AdvanceAsync(w, first.Id, 300_000, day.AddDays(2));

        var page1 = await ExpectAsync(await w.Owner.GetAsync("/api/salary-advances?pageSize=2"), HttpStatusCode.OK);
        var page2 = await ExpectAsync(await w.Owner.GetAsync("/api/salary-advances?pageSize=2&page=2"), HttpStatusCode.OK);
        var outstanding = await ExpectAsync(await w.Owner.GetAsync("/api/salary-advances?outstanding=true"), HttpStatusCode.OK);

        Assert.Equal((2, 2, 3), (page1.GetProperty("pageSize").GetInt32(), page2.GetProperty("page").GetInt32(),page1.GetProperty("total").GetInt32()));
        Assert.Equal([a, b], Ids(page1));
        Assert.Equal([c], Ids(page2));
        Assert.Equal(300_000m, page2.GetProperty("items")[0].GetProperty("remaining").GetDecimal());
        Assert.Equal(3, outstanding.GetProperty("total").GetInt32());
    }

    [Fact]
    public async Task The_person_filter_still_works_and_the_page_size_and_page_are_bounded()
    {
        var w = await factory.WorldAsync();
        var day = NewMonth().Day(2);
        var (mine, other) = (await w.AddWorkerAsync(), await w.AddWorkerAsync());
        var one = await AdvanceAsync(w, mine.Id, 100_000, day);
        await AdvanceAsync(w, other.Id, 100_000, day);

        var filtered = await ExpectAsync(await w.Owner.GetAsync($"/api/salary-advances?workerId={mine.Id}"), HttpStatusCode.OK);
        var huge = await ExpectAsync(await w.Owner.GetAsync($"/api/salary-advances?pageSize=100000&page={int.MaxValue}"), HttpStatusCode.OK);

        Assert.Equal([one], Ids(filtered));
        Assert.Equal(1, filtered.GetProperty("total").GetInt32());
        Assert.Equal(100, huge.GetProperty("pageSize").GetInt32());
        Assert.Empty(huge.GetProperty("items").EnumerateArray());
    }
}
