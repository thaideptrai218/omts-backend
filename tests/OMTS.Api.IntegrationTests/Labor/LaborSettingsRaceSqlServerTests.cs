using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using OMTS.Api.IntegrationTests.Infrastructure;
using static OMTS.Api.IntegrationTests.Infrastructure.AcsTestKit;
using static OMTS.Api.IntegrationTests.Infrastructure.FarmLabSqlServerKit;
using static OMTS.Api.IntegrationTests.Labor.LaborSettingsTestKit;

namespace OMTS.Api.IntegrationTests.Labor;

/// <summary>Parallel saves of the attendance and payroll settings on a real SQL Server, and stale versions on every route: 409, never 500.</summary>
public class LaborSettingsRaceSqlServerTests
{
    private const int Parallel = 8;

    [SqlServerFact]
    public async Task Parallel_attendance_settings_leave_one_version_on_one_date_or_on_different_dates()
    {
        await using var database = SqlServerTestDatabase.Create();
        await using var api = await StartAsync(database);
        var (owner, _, _) = await OwnerWithCooperativeAsync(api);
        var version = (await owner.GetFromJsonAsync<JsonElement>(LaborSettingsTestKit.AttendanceSettings)).VersionOf();

        var sameDate = await InParallelAsync(Parallel, i => owner.PostAsJsonAsync(LaborSettingsTestKit.AttendanceSettings, new { roundingUnit = i % 2 == 0 ? "Minutes15" : "Hour1", effectiveFrom = "2020-03-01", rowVersion = version }));

        await AssertOneWinnerAsync(sameDate, HttpStatusCode.OK, "MSG06");
        Assert.Equal(1, await QueryAsync(api, db => db.AttendanceSettingsVersions.CountAsync()));

        version = (await owner.GetFromJsonAsync<JsonElement>(LaborSettingsTestKit.AttendanceSettings)).VersionOf();
        var dates = await InParallelAsync(Parallel, i => owner.PostAsJsonAsync(LaborSettingsTestKit.AttendanceSettings, new { roundingUnit = "Minutes30", effectiveFrom = $"2021-03-{i + 1:00}", rowVersion = version }));

        await AssertOneWinnerAsync(dates, HttpStatusCode.OK, "MSG06");
        Assert.Equal(2, await QueryAsync(api, db => db.AttendanceSettingsVersions.CountAsync()));
    }

    [SqlServerFact]
    public async Task Parallel_payroll_settings_answer_msg06_to_all_but_one()
    {
        await using var database = SqlServerTestDatabase.Create();
        await using var api = await StartAsync(database);
        var (owner, _, _) = await OwnerWithCooperativeAsync(api);
        var version = (await owner.GetFromJsonAsync<JsonElement>(PayrollSettings)).VersionOf();

        var responses = await InParallelAsync(Parallel, i => owner.PutAsJsonAsync(PayrollSettings, new { payPeriodStartDay = i + 2, automaticPayroll = false, rowVersion = version }));

        await AssertOneWinnerAsync(responses, HttpStatusCode.OK, "MSG06");
        Assert.Equal(1, await QueryAsync(api, db => db.AuditEvents.CountAsync(e => e.Action == "PayrollSettings.Changed")));
    }

    [SqlServerFact]
    public async Task A_stale_version_answers_msg06_with_the_current_one_and_changes_nothing()
    {
        await using var database = SqlServerTestDatabase.Create();
        await using var api = await StartAsync(database);
        var (owner, account, _) = await OwnerWithCooperativeAsync(api);
        var old = await AddJobTypeAsync(owner);
        var moved = (await (await owner.PostRateAsync(old, 50_000, "2021-01-01")).BodyAsync(HttpStatusCode.OK)).GetProperty("jobType");
        Assert.NotEqual(old.VersionOf(), moved.VersionOf());
        var path = $"{JobTypes}/{old.IdOf()}";

        var responses = new[]
        {
            await owner.PostRateAsync(old, 60_000, "2022-01-01"),
            await owner.PutAsJsonAsync($"{path}/name", new { name = NewCode(), rowVersion = old.VersionOf() }),
            await owner.PostAsJsonAsync($"{path}/discontinue", new { rowVersion = old.VersionOf() }),
        };

        foreach (var response in responses)
        {
            var problem = await response.SrsProblemAsync(HttpStatusCode.Conflict, "MSG06", null);
            Assert.StartsWith($"The data was just updated by {account.DisplayName} at ", problem.Detail());
            Assert.Equal(moved.VersionOf(), problem.Str("currentRowVersion"));
        }

        var view = await owner.ReadJobTypeAsync(old.IdOf());
        Assert.Equal((old.Str("name"), "In Use", 2), (view.Str("name"), view.Str("status"), RatesOf(view).Count));
    }

    [SqlServerFact]
    public async Task The_settings_go_by_the_version_of_the_cooperative_and_a_stale_one_changes_nothing()
    {
        await using var database = SqlServerTestDatabase.Create();
        await using var api = await StartAsync(database);
        var (owner, _, _) = await OwnerWithCooperativeAsync(api);
        var stale = (await owner.GetFromJsonAsync<JsonElement>(LaborSettingsTestKit.AttendanceSettings)).VersionOf();
        Assert.Equal(stale, (await owner.GetFromJsonAsync<JsonElement>(PayrollSettings)).VersionOf());

        Assert.Equal(HttpStatusCode.OK, (await owner.PostAsJsonAsync(LaborSettingsTestKit.AttendanceSettings, new { roundingUnit = "Minutes15", effectiveFrom = "2020-03-01", rowVersion = stale })).StatusCode);

        await (await owner.PostAsJsonAsync(LaborSettingsTestKit.AttendanceSettings, new { roundingUnit = "Hour1", effectiveFrom = "2021-03-01", rowVersion = stale })).SrsProblemAsync(HttpStatusCode.Conflict, "MSG06", null);
        await (await owner.PutAsJsonAsync(PayrollSettings, new { payPeriodStartDay = 26, automaticPayroll = true, rowVersion = stale })).SrsProblemAsync(HttpStatusCode.Conflict, "MSG06", null);
        Assert.Equal(1, await QueryAsync(api, db => db.AttendanceSettingsVersions.CountAsync()));
        Assert.Equal(1, await QueryAsync(api, db => db.Cooperatives.Select(c => c.PayPeriodStartDay).SingleAsync()));
    }
}
