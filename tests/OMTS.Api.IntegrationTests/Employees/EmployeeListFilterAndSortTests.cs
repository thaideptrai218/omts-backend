using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using OMTS.Api.IntegrationTests.Infrastructure;
using OMTS.Domain.Enums;
using static OMTS.Api.IntegrationTests.Employees.EmployeeRequests;
using static OMTS.Api.IntegrationTests.Infrastructure.AcsTestKit;
using static OMTS.Api.IntegrationTests.Purchasing.PurchasingTestKit;

namespace OMTS.Api.IntegrationTests.Employees;

/// <summary>EMP-API-01 and EMP-API-02: the employee list filtered by type and job type, sorted on the server over the whole result, and searched by code.</summary>
public class EmployeeListFilterAndSortTests(AuthApiFactory factory) : IClassFixture<AuthApiFactory>
{
    private const string Url = "/api/employees";

    private static JsonElement[] Items(JsonElement page) => page.GetProperty("items").EnumerateArray().ToArray();

    private static string[] Names(JsonElement page) => Items(page).Select(i => i.GetProperty("fullName").GetString()!).ToArray();

    private static async Task<JsonElement> ListAsync(HttpClient client, string query) =>
        await (await client.GetAsync($"{Url}?{query}")).ReadAsync(HttpStatusCode.OK);

    /// <summary>Three employees with the tag in their names, A to C: A seasonal with job type 1, B Staff with job type 2, C seasonal with none.</summary>
    private async Task<(HttpClient Owner, string Tag, Guid Zone, Guid Job1, Guid Job2, Guid[] Ids)> SeedAsync()
    {
        var (owner, zone, _) = await SetupAsync(factory);
        var tag = NewCode();
        var job1 = await AddJobTypeAsync(factory);
        var job2 = await AddJobTypeAsync(factory);
        var a = IdOf(await CreateAsync(owner, Profile($"{tag} A", NewPhone(), zone.Id, b => b["jobTypeIds"] = new[] { job1.Id })));
        var b = IdOf(await CreateAsync(owner, Profile($"{tag} B", NewPhone(), zone.Id, x => { x["employeeType"] = "Staff"; x["jobTypeIds"] = new[] { job2.Id }; })));
        var c = IdOf(await CreateAsync(owner, Profile($"{tag} C", null, zone.Id)));
        return (owner, tag, zone.Id, job1.Id, job2.Id, [a, b, c]);
    }

    [Fact]
    public async Task The_list_is_filtered_by_employee_type_and_by_job_type_alone_and_together()
    {
        var (owner, tag, _, job1, job2, ids) = await SeedAsync();

        var staff = await ListAsync(owner, $"search={tag}&employeeType=Staff");
        var seasonal = await ListAsync(owner, $"search={tag}&employeeType=SeasonalWorker");
        var byJob = await ListAsync(owner, $"search={tag}&jobTypeId={job1}");
        var both = await ListAsync(owner, $"search={tag}&jobTypeId={job2}&employeeType=SeasonalWorker");

        Assert.Equal([$"{tag} B"], Names(staff));
        Assert.Equal([$"{tag} A", $"{tag} C"], Names(seasonal));
        Assert.Equal([$"{tag} A"], Names(byJob));
        Assert.Equal(0, both.GetProperty("total").GetInt32());
        Assert.Equal(ids[1], Items(staff)[0].Id());
    }

    [Fact]
    public async Task The_type_filter_reads_the_type_in_force_today_not_an_earlier_or_later_one()
    {
        var (owner, zone, _) = await SetupAsync(factory);
        var tag = NewCode();
        var id = IdOf(await CreateAsync(owner, Profile($"{tag} E", null, zone.Id, b => { b["employeeType"] = "SeasonalWorker"; b["employeeTypeFrom"] = "2026-01-01"; })));
        var version = await CurrentVersionAsync(owner, id);
        var changed = await owner.PutAsJsonAsync($"{Url}/{id}", Profile($"{tag} E", null, zone.Id, b =>
        {
            b["employeeType"] = "Staff";
            b["employeeTypeFrom"] = EmployeeRequests.Today().AddDays(30).ToString("yyyy-MM-dd");
            b["rowVersion"] = version;
        }));
        Assert.Equal(HttpStatusCode.OK, changed.StatusCode);

        // The Staff period starts in 30 days: today the employee is still a seasonal worker.
        Assert.Equal([$"{tag} E"], Names(await ListAsync(owner, $"search={tag}&employeeType=SeasonalWorker")));
        Assert.Equal(0, (await ListAsync(owner, $"search={tag}&employeeType=Staff")).GetProperty("total").GetInt32());
    }

    [Theory]
    [InlineData("employeeType=Contractor")]
    [InlineData("employeeType=7")]
    [InlineData("jobTypeId=not-a-guid")]
    [InlineData("sortBy=salary")]
    [InlineData("sortBy=Name")]
    [InlineData("sortDir=up")]
    [InlineData("sortBy=name&sortDir=DESC")]
    public async Task A_value_that_does_not_exist_is_refused_with_400(string query)
    {
        var (owner, _, _) = await SetupAsync(factory);

        var response = await owner.GetAsync($"{Url}?{query}");

        await response.ProblemAsync(HttpStatusCode.BadRequest, "MSG-GEN-01");
    }

    [Fact]
    public async Task The_sort_fields_are_named_in_the_refusal()
    {
        var (owner, _, _) = await SetupAsync(factory);

        Assert.Equal("Sort by", await (await owner.GetAsync($"{Url}?sortBy=salary")).FieldAsync(HttpStatusCode.BadRequest, "MSG-GEN-01"));
        Assert.Equal("Sort direction", await (await owner.GetAsync($"{Url}?sortDir=up")).FieldAsync(HttpStatusCode.BadRequest, "MSG-GEN-01"));
        Assert.NotNull(await (await owner.GetAsync($"{Url}?employeeType=7")).FieldAsync(HttpStatusCode.BadRequest, "MSG-GEN-01"));
    }

    [Fact]
    public async Task Sorting_by_name_phone_type_and_code_runs_over_the_whole_result_in_both_directions()
    {
        var (owner, tag, _, _, _, _) = await SeedAsync();

        Assert.Equal([$"{tag} A", $"{tag} B", $"{tag} C"], Names(await ListAsync(owner, $"search={tag}&sortBy=name&sortDir=asc")));
        Assert.Equal([$"{tag} C", $"{tag} B", $"{tag} A"], Names(await ListAsync(owner, $"search={tag}&sortBy=name&sortDir=desc")));
        // C has no phone number: no value comes first when ascending.
        Assert.Equal($"{tag} C", Names(await ListAsync(owner, $"search={tag}&sortBy=phone"))[0]);
        Assert.Equal($"{tag} C", Names(await ListAsync(owner, $"search={tag}&sortBy=phone&sortDir=desc"))[^1]);
        // Seasonal workers (A, C) sort before Staff (B); each pair ends by id, so the order is stable.
        var byType = Names(await ListAsync(owner, $"search={tag}&sortBy=type"));
        Assert.Equal($"{tag} B", byType[^1]);
        Assert.Equal($"{tag} B", Names(await ListAsync(owner, $"search={tag}&sortBy=type&sortDir=desc"))[0]);
        // Codes are given in the order of creation.
        Assert.Equal([$"{tag} A", $"{tag} B", $"{tag} C"], Names(await ListAsync(owner, $"search={tag}&sortBy=code")));
        Assert.Equal([$"{tag} C", $"{tag} B", $"{tag} A"], Names(await ListAsync(owner, $"search={tag}&sortBy=code&sortDir=desc")));
    }

    [Fact]
    public async Task Sorting_by_status_and_last_working_day_puts_the_left_employee_where_the_direction_says()
    {
        var (owner, tag, _, _, _, ids) = await SeedAsync();
        await EndAsync(owner, ids[1], EmployeeRequests.Today().AddDays(-3));

        var byStatus = Names(await ListAsync(owner, $"search={tag}&sortBy=status"));
        var byStatusDesc = Names(await ListAsync(owner, $"search={tag}&sortBy=status&sortDir=desc"));
        var byDay = Names(await ListAsync(owner, $"search={tag}&sortBy=lastWorkingDay"));
        var byDayDesc = Names(await ListAsync(owner, $"search={tag}&sortBy=lastWorkingDay&sortDir=desc"));

        Assert.Equal($"{tag} B", byStatus[^1]);
        Assert.Equal($"{tag} B", byStatusDesc[0]);
        Assert.Equal($"{tag} B", byDay[^1]);
        Assert.Equal($"{tag} B", byDayDesc[0]);
    }

    [Fact]
    public async Task Pages_of_a_sorted_list_follow_each_other_without_gaps_or_repeats()
    {
        var (owner, zone, _) = await SetupAsync(factory);
        var tag = NewCode();
        // Five employees with the same type: the order inside it ends by id.
        for (var i = 0; i < 5; i++)
        {
            await CreateAsync(owner, Profile($"{tag} {i}", null, zone.Id));
        }

        var whole = Items(await ListAsync(owner, $"search={tag}&sortBy=type&pageSize=100")).Select(i => i.Id()).ToList();
        var paged = new List<Guid>();
        for (var page = 1; page <= 3; page++)
        {
            paged.AddRange(Items(await ListAsync(owner, $"search={tag}&sortBy=type&pageSize=2&page={page}")).Select(i => i.Id()));
        }

        Assert.Equal(5, whole.Count);
        Assert.Equal(whole, paged);
    }

    [Fact]
    public async Task Without_a_sort_the_list_keeps_its_name_order_and_a_zone_manager_still_gets_their_zone_first()
    {
        var (owner, zone, _) = await SetupAsync(factory);
        var other = await factory.AddZoneAsync(NewCode());
        var manager = await ManagerAsync(factory, zone);
        var tag = NewCode();
        await CreateAsync(owner, Profile($"{tag} A in other zone", null, other.Id));
        await CreateAsync(owner, Profile($"{tag} B in own zone", null, zone.Id));

        var byOwner = Names(await ListAsync(owner, $"search={tag}"));
        var byManager = Names(await ListAsync(manager, $"search={tag}"));
        var sortedByManager = Names(await ListAsync(manager, $"search={tag}&sortBy=name"));

        Assert.Equal([$"{tag} A in other zone", $"{tag} B in own zone"], byOwner);
        Assert.Equal([$"{tag} B in own zone", $"{tag} A in other zone"], byManager);
        Assert.Equal([$"{tag} A in other zone", $"{tag} B in own zone"], sortedByManager);
    }

    [Fact]
    public async Task The_search_also_finds_an_employee_by_code()
    {
        var (owner, zone, _) = await SetupAsync(factory);
        var code = $"K{NewCode()[1..8]}".ToUpperInvariant();
        var created = await CreateAsync(owner, Profile($"By code {NewCode()}", null, zone.Id, b => b["code"] = code));

        var found = await ListAsync(owner, $"search={code}");

        Assert.Equal([IdOf(created)], Items(found).Select(i => i.Id()));
        Assert.Equal(code, Items(found)[0].GetProperty("code").GetString());
    }
}
