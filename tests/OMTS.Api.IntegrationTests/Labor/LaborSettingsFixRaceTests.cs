using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using OMTS.Api.IntegrationTests.Infrastructure;
using OMTS.Application.Common.Interfaces.Persistence;
using OMTS.Domain.Entities.Labor;
using OMTS.Infrastructure.Persistence;
using OMTS.Infrastructure.Persistence.Repositories;
using static OMTS.Api.IntegrationTests.Infrastructure.FarmLabSqlServerKit;
using static OMTS.Api.IntegrationTests.Labor.LaborSettingsQaKit;
using static OMTS.Api.IntegrationTests.Labor.LaborSettingsTestKit;

namespace OMTS.Api.IntegrationTests.Labor;

/// <summary>
/// QA L3 of OMTS-227 on a real SQL Server: identical saves in parallel have one winner, each loser answers MSG06, and an answer of
/// 200 never holds an old <c>rowVersion</c>. One save that changes nothing, in the moment another lands, also answers MSG06.
/// </summary>
public class LaborSettingsFixRaceTests
{
    private const int Parallel = 8;
    private const int Rounds = 5;

    /// <summary>The payroll headers of the host, with one moment before the lock reads them: the point just before a rate is compared.</summary>
    private sealed class MomentPayrolls(IPayrollRepository inner, Func<Func<Task>?> take) : IPayrollRepository
    {
        public async Task<IReadOnlyList<Payroll>> ListEndingOnOrAfterAsync(DateOnly date, CancellationToken cancellationToken)
        {
            if (take() is { } moment)
            {
                await moment();
            }

            return await inner.ListEndingOnOrAfterAsync(date, cancellationToken);
        }

        public Task<DateOnly?> LastPeriodEndAsync(CancellationToken cancellationToken) => inner.LastPeriodEndAsync(cancellationToken);
    }

    [SqlServerFact]
    public async Task Parallel_identical_attendance_settings_have_one_winner_whose_version_is_the_record_s()
    {
        await using var database = SqlServerTestDatabase.Create();
        await using var api = await StartAsync(database);
        var (owner, _, _) = await OwnerWithCooperativeAsync(api);

        for (var round = 0; round < Rounds; round++)
        {
            var version = (await owner.GetFromJsonAsync<JsonElement>(AttendanceSettings)).VersionOf();
            var date = $"202{round}-03-01";

            var responses = await InParallelAsync(Parallel, _ => owner.PostAsJsonAsync(AttendanceSettings, new { roundingUnit = "Minutes15", effectiveFrom = date, rowVersion = version }));

            await AssertOneWinnerAsync(responses, HttpStatusCode.OK, "MSG06");
            var winner = await responses.Single(r => r.StatusCode == HttpStatusCode.OK).BodyAsync(HttpStatusCode.OK);
            Assert.Equal((await owner.GetFromJsonAsync<JsonElement>(AttendanceSettings)).VersionOf(), winner.GetProperty("settings").VersionOf());
            Assert.Equal(round + 1, await QueryAsync(api, db => db.AttendanceSettingsVersions.CountAsync()));
        }
    }

    [SqlServerFact]
    public async Task Parallel_identical_rates_have_one_winner_whose_version_is_the_record_s()
    {
        await using var database = SqlServerTestDatabase.Create();
        await using var api = await StartAsync(database);
        var (owner, _, _) = await OwnerWithCooperativeAsync(api);

        for (var round = 0; round < Rounds; round++)
        {
            var jobType = await AddJobTypeAsync(owner, from: "2019-01-01");

            var responses = await InParallelAsync(Parallel, _ => owner.PostRateAsync(jobType, 50_000, "2020-03-01"));

            await AssertOneWinnerAsync(responses, HttpStatusCode.OK, "MSG06");
            var winner = await responses.Single(r => r.StatusCode == HttpStatusCode.OK).BodyAsync(HttpStatusCode.OK);
            Assert.Equal((await owner.ReadJobTypeAsync(jobType.IdOf())).VersionOf(), winner.GetProperty("jobType").VersionOf());
            Assert.Equal(2, await QueryAsync(api, db => db.PayRates.CountAsync(r => r.JobTypeId == jobType.IdOf())));
        }
    }

    // One row holds both the values and the version, so payroll settings have no such gap; this keeps it so.
    [SqlServerFact]
    public async Task Parallel_identical_payroll_settings_have_one_winner_whose_version_is_the_record_s()
    {
        await using var database = SqlServerTestDatabase.Create();
        await using var api = await StartAsync(database);
        var (owner, _, _) = await OwnerWithCooperativeAsync(api);

        for (var round = 0; round < Rounds; round++)
        {
            var version = (await owner.GetFromJsonAsync<JsonElement>(PayrollSettings)).VersionOf();

            var responses = await InParallelAsync(Parallel, _ => owner.PutAsJsonAsync(PayrollSettings, new { payPeriodStartDay = round + 2, automaticPayroll = false, rowVersion = version }));

            await AssertOneWinnerAsync(responses, HttpStatusCode.OK, "MSG06");
            var winner = await responses.Single(r => r.StatusCode == HttpStatusCode.OK).BodyAsync(HttpStatusCode.OK);
            Assert.Equal((await owner.GetFromJsonAsync<JsonElement>(PayrollSettings)).VersionOf(), winner.GetProperty("settings").VersionOf());
        }
    }

    // The save has nothing to change, so no save checks the version for it: a rename landing here must not leave it holding an old one.
    [SqlServerFact]
    public async Task The_same_rate_saved_as_another_save_lands_does_not_hand_back_a_stale_version()
    {
        await using var database = SqlServerTestDatabase.Create();
        await using var api = await StartAsync(database);
        Func<Task>? moment = null;
        Func<Task>? Take()
        {
            var taken = moment;
            moment = null;
            return taken;
        }

        await using var app = api.WithWebHostBuilder(builder => builder.ConfigureTestServices(services =>
        {
            services.RemoveAll<IPayrollRepository>();
            services.AddScoped<IPayrollRepository>(provider => new MomentPayrolls(new PayrollRepository(provider.GetRequiredService<OmtsDbContext>()), Take));
        }));
        await OwnerWithCooperativeAsync(api);
        var (owner, _) = await LoginNewAsync(app);
        var jobType = await AddJobTypeAsync(owner, rate: 50_000, from: "2020-03-01");
        string? moved = null;
        moment = async () => moved = (await (await owner.PutAsJsonAsync($"{JobTypes}/{jobType.IdOf()}/name", new { name = $"Đổi {jobType.IdOf():N}", rowVersion = jobType.VersionOf() })).BodyAsync(HttpStatusCode.OK)).GetProperty("jobType").VersionOf();

        var same = await owner.PostRateAsync(jobType, 50_000, "2020-03-01");

        var problem = await same.SrsProblemAsync(HttpStatusCode.Conflict, "MSG06", null);
        Assert.NotNull(moved);
        Assert.Equal(moved, problem.Str("currentRowVersion"));
    }

    [SqlServerFact]
    public async Task The_same_rate_under_the_current_version_still_answers_200_and_moves_nothing()
    {
        await using var database = SqlServerTestDatabase.Create();
        await using var api = await StartAsync(database);
        var (owner, _, _) = await OwnerWithCooperativeAsync(api);
        var jobType = await AddJobTypeAsync(owner, rate: 50_000, from: "2020-03-01");

        var same = await owner.PostRateAsync(jobType, 50_000, "2020-03-01");

        Assert.Equal(jobType.VersionOf(), (await same.BodyAsync(HttpStatusCode.OK)).GetProperty("jobType").VersionOf());
        Assert.Equal(0, await QueryAsync(api, db => db.AuditEvents.CountAsync(e => e.RecordId == jobType.IdOf() && e.Action == "JobType.RateChanged")));
    }
}
