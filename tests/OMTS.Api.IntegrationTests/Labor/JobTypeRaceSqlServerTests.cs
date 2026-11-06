using System.Net;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using OMTS.Api.IntegrationTests.Infrastructure;
using static OMTS.Api.IntegrationTests.Infrastructure.AcsTestKit;
using static OMTS.Api.IntegrationTests.Infrastructure.FarmLabSqlServerKit;
using static OMTS.Api.IntegrationTests.Labor.LaborSettingsTestKit;

namespace OMTS.Api.IntegrationTests.Labor;

/// <summary>Parallel saves of job types and rates on a real SQL Server: the row version and the unique indexes keep one winner; the others answer 409, never 500.</summary>
public class JobTypeRaceSqlServerTests
{
    private const int Parallel = 8;

    [SqlServerFact]
    public async Task Parallel_adds_of_one_name_leave_one_job_type_and_the_others_get_msg_gen_02()
    {
        await using var database = SqlServerTestDatabase.Create();
        await using var api = await StartAsync(database);
        var (owner, _, _) = await OwnerWithCooperativeAsync(api);
        var name = $"Thu hoạch {NewCode()}";

        var responses = await InParallelAsync(Parallel, i => owner.PostAsJsonAsync(JobTypes, new { name = i % 2 == 0 ? name : name.ToUpperInvariant(), hourlyRate = 40_000 + i, effectiveFrom = "2020-01-01" }));

        await AssertOneWinnerAsync(responses, HttpStatusCode.Created, "MSG-GEN-02");
        Assert.Equal(1, await QueryAsync(api, db => db.JobTypes.CountAsync()));
        Assert.Equal(1, await QueryAsync(api, db => db.PayRates.CountAsync()));
        Assert.Equal(1, await QueryAsync(api, db => db.AuditEvents.CountAsync(e => e.Action == "JobType.Created")));
        var winner = await QueryAsync(api, db => db.JobTypes.SingleAsync());
        foreach (var loser in responses.Where(r => r.StatusCode == HttpStatusCode.Conflict))
        {
            Assert.Equal(winner.Id, (await loser.SrsProblemAsync(HttpStatusCode.Conflict, "MSG-GEN-02", "MSG03")).GetProperty("existingId").GetGuid());
        }
    }

    [SqlServerFact]
    public async Task Parallel_rates_of_one_job_type_on_one_date_leave_one_rate_and_the_others_get_msg06()
    {
        await using var database = SqlServerTestDatabase.Create();
        await using var api = await StartAsync(database);
        var (owner, _, _) = await OwnerWithCooperativeAsync(api);
        var jobType = await AddJobTypeAsync(owner);

        var responses = await InParallelAsync(Parallel, i => owner.PostRateAsync(jobType, 50_000 + i, "2021-01-01"));

        await AssertOneWinnerAsync(responses, HttpStatusCode.OK, "MSG06");
        Assert.Equal(2, await QueryAsync(api, db => db.PayRates.CountAsync()));
        Assert.Equal(1, await QueryAsync(api, db => db.AuditEvents.CountAsync(e => e.Action == "JobType.RateChanged")));
    }

    [SqlServerFact]
    public async Task Parallel_rates_of_one_job_type_on_different_dates_answer_msg06_to_all_but_one()
    {
        await using var database = SqlServerTestDatabase.Create();
        await using var api = await StartAsync(database);
        var (owner, _, _) = await OwnerWithCooperativeAsync(api);
        var jobType = await AddJobTypeAsync(owner);

        var responses = await InParallelAsync(Parallel, i => owner.PostRateAsync(jobType, 50_000, $"2021-01-{i + 1:00}"));

        await AssertOneWinnerAsync(responses, HttpStatusCode.OK, "MSG06");
        Assert.Equal(2, await QueryAsync(api, db => db.PayRates.CountAsync()));
    }

    [SqlServerFact]
    public async Task Parallel_replaces_of_one_rate_answer_msg06_to_all_but_one()
    {
        await using var database = SqlServerTestDatabase.Create();
        await using var api = await StartAsync(database);
        var (owner, _, _) = await OwnerWithCooperativeAsync(api);
        var jobType = await AddJobTypeAsync(owner);

        var responses = await InParallelAsync(Parallel, i => owner.PostRateAsync(jobType, 50_000 + i, "2020-01-01"));

        await AssertOneWinnerAsync(responses, HttpStatusCode.OK, "MSG06");
        Assert.Equal(1, await QueryAsync(api, db => db.PayRates.CountAsync()));
    }

    [SqlServerFact]
    public async Task Parallel_renames_to_one_name_and_discontinues_never_answer_500()
    {
        await using var database = SqlServerTestDatabase.Create();
        await using var api = await StartAsync(database);
        var (owner, _, _) = await OwnerWithCooperativeAsync(api);
        var (one, two) = (await AddJobTypeAsync(owner), await AddJobTypeAsync(owner));
        var name = $"Tên {NewCode()}";

        var responses = await Task.WhenAll(
            owner.PutAsJsonAsync($"{JobTypes}/{one.IdOf()}/name", new { name, rowVersion = one.VersionOf() }),
            owner.PutAsJsonAsync($"{JobTypes}/{two.IdOf()}/name", new { name, rowVersion = two.VersionOf() }),
            owner.PostAsJsonAsync($"{JobTypes}/{one.IdOf()}/discontinue", new { rowVersion = one.VersionOf() }),
            owner.PostAsJsonAsync($"{JobTypes}/{two.IdOf()}/discontinue", new { rowVersion = two.VersionOf() }));

        await AssertNo500Async(responses);
        Assert.True(await QueryAsync(api, db => db.JobTypes.CountAsync(j => j.Name == name)) <= 1);
    }
}
