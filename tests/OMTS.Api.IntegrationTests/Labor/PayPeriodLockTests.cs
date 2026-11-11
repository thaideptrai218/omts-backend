using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using OMTS.Api.IntegrationTests.Infrastructure;
using OMTS.Domain.Constants;
using static OMTS.Api.IntegrationTests.Infrastructure.AcsTestKit;
using static OMTS.Api.IntegrationTests.Labor.LaborSettingsTestKit;

namespace OMTS.Api.IntegrationTests.Labor;

/// <summary>
/// BR-LABOR-13 and MSG-LABOR-04 against stored payrolls: rates, attendance settings and employee types take effect only after
/// the last day of every pay period that is sent or finalized. Each test has its own host, as a payroll locks for the whole database.
/// </summary>
public class PayPeriodLockTests
{
    private const string October = "2020-10-01";
    private const string OctoberEnd = "2020-10-31";
    private const string NovemberFirst = "2020-11-01";

    private static Task<(HttpClient Owner, JsonElement JobType)> OwnerWithJobTypeAsync(AuthApiFactory factory) => OwnerWithJobTypeAsync(factory, "2019-01-01");

    private static async Task<(HttpClient Owner, JsonElement JobType)> OwnerWithJobTypeAsync(AuthApiFactory factory, string from)
    {
        var owner = await OwnerAsync(factory);
        return (owner, await AddJobTypeAsync(owner, from: from));
    }

    [Fact]
    public async Task A_rate_on_the_last_day_of_a_payroll_awaiting_finalization_is_refused_and_the_next_day_is_saved()
    {
        await using var factory = new AuthApiFactory();
        var (owner, jobType) = await OwnerWithJobTypeAsync(factory);
        await factory.AddAwaitingPayrollAsync(October, OctoberEnd);

        var refused = await owner.PostRateAsync(jobType, 50_000, OctoberEnd);
        var saved = await owner.PostRateAsync(jobType, 50_000, NovemberFirst);

        var problem = await refused.SrsProblemAsync(HttpStatusCode.Conflict, "MSG-LABOR-04", null);
        Assert.Equal(
            "The payroll of 10/2020 is Awaiting finalization, so this change cannot apply to 31/10/2020. Choose a date after 31/10/2020, or ask the Cooperative Owner to send the payroll back first.",
            problem.Detail());
        Assert.Equal(OctoberEnd, problem.Str("lockedUntil"));
        Assert.Equal(HttpStatusCode.OK, saved.StatusCode);
        Assert.Equal(2, RatesOf(await owner.ReadJobTypeAsync(jobType.IdOf())).Count);
    }

    [Fact]
    public async Task A_finalized_payroll_refuses_for_good_and_offers_no_send_back()
    {
        await using var factory = new AuthApiFactory();
        var (owner, jobType) = await OwnerWithJobTypeAsync(factory);
        await factory.AddPayrollAsync(October, OctoberEnd, StatusIds.Payroll.Finalized);

        var refused = await owner.PostRateAsync(jobType, 50_000, October);

        var problem = await refused.SrsProblemAsync(HttpStatusCode.Conflict, "MSG-LABOR-04", null);
        Assert.Equal("The payroll of 10/2020 is Finalized, so this change cannot apply to 01/10/2020. Choose a date after 31/10/2020.", problem.Detail());
    }

    [Theory]
    [InlineData(StatusIds.Payroll.Draft)]
    [InlineData(StatusIds.Payroll.SentBack)]
    public async Task A_payroll_not_sent_does_not_lock_its_period_and_the_save_touches_it(int statusId)
    {
        await using var factory = new AuthApiFactory();
        var (owner, jobType) = await OwnerWithJobTypeAsync(factory);
        var payroll = await factory.AddPayrollAsync(October, OctoberEnd, statusId);

        var saved = await owner.PostRateAsync(jobType, 50_000, "2020-10-15");

        Assert.Equal(HttpStatusCode.OK, saved.StatusCode);
        // The row is written by the same save, so a parallel send meets a new row version (see the SQL Server tests).
        Assert.NotNull(await factory.QueryAsync(db => db.Payrolls.Where(p => p.Id == payroll.Id).Select(p => p.UpdatedAt).SingleAsync()));
    }

    [Fact]
    public async Task A_payroll_that_ended_before_the_date_is_not_touched()
    {
        await using var factory = new AuthApiFactory();
        var (owner, jobType) = await OwnerWithJobTypeAsync(factory);
        var payroll = await factory.AddPayrollAsync(October, OctoberEnd, StatusIds.Payroll.Draft);

        Assert.Equal(HttpStatusCode.OK, (await owner.PostRateAsync(jobType, 50_000, NovemberFirst)).StatusCode);

        Assert.Null(await factory.QueryAsync(db => db.Payrolls.Where(p => p.Id == payroll.Id).Select(p => p.UpdatedAt).SingleAsync()));
    }

    [Fact]
    public async Task A_date_in_a_draft_period_is_refused_when_a_later_payroll_is_sent()
    {
        await using var factory = new AuthApiFactory();
        var (owner, jobType) = await OwnerWithJobTypeAsync(factory);
        await factory.AddPayrollAsync(October, OctoberEnd, StatusIds.Payroll.Draft);
        await factory.AddAwaitingPayrollAsync(NovemberFirst, "2020-11-30");

        var refused = await owner.PostRateAsync(jobType, 50_000, "2020-10-15");

        var problem = await refused.SrsProblemAsync(HttpStatusCode.Conflict, "MSG-LABOR-04", null);
        Assert.StartsWith("The payroll of 11/2020 is Awaiting finalization, so this change cannot apply to 15/10/2020.", problem.Detail());
        Assert.Equal("2020-11-30", problem.Str("lockedUntil"));
    }

    [Fact]
    public async Task With_a_finalized_payroll_in_the_way_sending_back_a_later_one_is_not_offered()
    {
        await using var factory = new AuthApiFactory();
        var (owner, jobType) = await OwnerWithJobTypeAsync(factory);
        await factory.AddPayrollAsync(October, OctoberEnd, StatusIds.Payroll.Finalized);
        await factory.AddAwaitingPayrollAsync(NovemberFirst, "2020-11-30");

        var refused = await owner.PostRateAsync(jobType, 50_000, "2020-10-15");

        Assert.EndsWith("Choose a date after 30/11/2020.", (await refused.SrsProblemAsync(HttpStatusCode.Conflict, "MSG-LABOR-04", null)).Detail());
    }

    [Fact]
    public async Task A_payroll_sent_back_lets_the_change_through_again()
    {
        await using var factory = new AuthApiFactory();
        var (owner, jobType) = await OwnerWithJobTypeAsync(factory);
        var payroll = await factory.AddAwaitingPayrollAsync(October, OctoberEnd);
        Assert.Equal("MSG-LABOR-04", await (await owner.PostRateAsync(jobType, 50_000, October)).ErrorCodeAsync());

        await factory.UpdateAsync<OMTS.Domain.Entities.Labor.Payroll>(payroll.Id, p => p.StatusId = StatusIds.Payroll.SentBack);

        Assert.Equal(HttpStatusCode.OK, (await owner.PostRateAsync(jobType, 50_000, October)).StatusCode);
    }

    [Fact]
    public async Task A_rate_with_the_date_of_a_rate_in_a_locked_period_does_not_replace_it()
    {
        await using var factory = new AuthApiFactory();
        var (owner, jobType) = await OwnerWithJobTypeAsync(factory, "2020-10-10");
        await factory.AddPayrollAsync(October, OctoberEnd, StatusIds.Payroll.Finalized);

        var refused = await owner.PostRateAsync(jobType, 99_000, "2020-10-10");

        Assert.Equal("MSG-LABOR-04", await refused.ErrorCodeAsync());
        Assert.Equal([(40_000m, "2020-10-10")], RatesOf(await owner.ReadJobTypeAsync(jobType.IdOf())));
    }

    [Fact]
    public async Task A_new_job_type_dated_in_a_locked_period_saves_nothing()
    {
        await using var factory = new AuthApiFactory();
        var owner = await OwnerAsync(factory);
        await factory.AddAwaitingPayrollAsync(October, OctoberEnd);
        var name = $"Mới {NewCode()}";

        var refused = await owner.PostAsJsonAsync(JobTypes, new { name, hourlyRate = 1, effectiveFrom = OctoberEnd });

        Assert.Equal("MSG-LABOR-04", await refused.ErrorCodeAsync());
        Assert.False(await factory.QueryAsync(db => db.JobTypes.AnyAsync(j => j.Name == name)));
        Assert.Equal(0, await factory.AuditCountAsync("JobType.Created"));
    }

    [Fact]
    public async Task Attendance_settings_dated_in_a_locked_period_are_refused()
    {
        await using var factory = new AuthApiFactory();
        var owner = await OwnerAsync(factory);
        await factory.AddAwaitingPayrollAsync(October, OctoberEnd);
        var version = (await owner.GetFromJsonAsync<JsonElement>(AttendanceSettings)).VersionOf();

        var refused = await owner.PostAsJsonAsync(AttendanceSettings, new { roundingUnit = "Minutes15", effectiveFrom = OctoberEnd, rowVersion = version });
        var saved = await owner.PostAsJsonAsync(AttendanceSettings, new { roundingUnit = "Minutes15", effectiveFrom = NovemberFirst, rowVersion = version });

        await refused.SrsProblemAsync(HttpStatusCode.Conflict, "MSG-LABOR-04", null);
        Assert.Equal(HttpStatusCode.OK, saved.StatusCode);
        Assert.Equal(1, await factory.QueryAsync(db => db.AttendanceSettingsVersions.CountAsync()));
    }
}
