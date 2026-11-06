using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using OMTS.Api.IntegrationTests.Infrastructure;
using OMTS.Application.Features.Labor;
using OMTS.Domain.Constants;
using OMTS.Domain.Entities.Labor;
using static OMTS.Api.IntegrationTests.Infrastructure.AcsTestKit;
using static OMTS.Api.IntegrationTests.Infrastructure.FarmLabSqlServerKit;
using static OMTS.Api.IntegrationTests.Labor.LaborSettingsQaKit;
using static OMTS.Api.IntegrationTests.Labor.LaborSettingsTestKit;

namespace OMTS.Api.IntegrationTests.Labor;

/// <summary>QA of OMTS-227, BR-LABOR-12 and GBR-02: money as it comes on the wire, effective dates on SQL Server, a history never rewritten, the audit before and after.</summary>
public class LaborSettingsQaRateTests(LaborSqlFixture sql) : IClassFixture<LaborSqlFixture>
{
    private Task<HttpResponseMessage> AddRawAsync(string rateJson, string dateJson = "\"2020-01-01\"") =>
        sql.Owner.PostRawAsync(JobTypes, $"{{\"name\":\"{NewCode()}\",\"hourlyRate\":{rateJson},\"effectiveFrom\":{dateJson}}}");

    private Task<HttpResponseMessage> RateRawAsync(JsonElement jobType, string rateJson, string dateJson = "\"2021-01-01\"") =>
        sql.Owner.PostRawAsync($"{JobTypes}/{jobType.IdOf()}/rates", $"{{\"hourlyRate\":{rateJson},\"effectiveFrom\":{dateJson},\"rowVersion\":\"{jobType.VersionOf()}\"}}");

    [SqlServerTheory]
    [InlineData("1", "1")]
    [InlineData("40000", "40000")]
    [InlineData("40000.0", "40000")]
    [InlineData("40000.000", "40000")]
    [InlineData("4e4", "40000")]
    [InlineData("4.5E4", "45000")]
    [InlineData("999999999", "999999999")]
    [InlineData("999999999.0", "999999999")]
    public async Task A_whole_amount_up_to_the_cap_is_stored_and_shown_whole(string json, string shown)
    {
        var added = await (await AddRawAsync(json)).BodyAsync(HttpStatusCode.Created);
        var jobType = added.GetProperty("jobType");
        var changed = await (await RateRawAsync(jobType, json)).BodyAsync(HttpStatusCode.OK);

        Assert.Equal(shown, jobType.GetProperty("currentRate").GetProperty("hourlyRate").GetRawText());
        Assert.Equal(shown, changed.GetProperty("jobType").GetProperty("currentRate").GetProperty("hourlyRate").GetRawText());
        var read = await sql.Owner.ReadJobTypeAsync(jobType.IdOf());
        Assert.Equal(shown, read.GetProperty("currentRate").GetProperty("hourlyRate").GetRawText());
    }

    [SqlServerTheory]
    [InlineData("0")]
    [InlineData("0.0")]
    [InlineData("-0")]
    [InlineData("-1")]
    [InlineData("-40000")]
    [InlineData("0.5")]
    [InlineData("40000.5")]
    [InlineData("40000.01")]
    [InlineData("1000000000")]
    [InlineData("1e9")]
    [InlineData("999999999.5")]
    [InlineData("1e28")]
    [InlineData("79228162514264337593543950335")]
    public async Task An_amount_outside_the_rule_is_msg_gen_01_on_hourly_rate(string json)
    {
        var jobType = await AddJobTypeAsync(sql.Owner);

        var added = await AddRawAsync(json);
        var changed = await RateRawAsync(jobType, json);

        foreach (var response in new[] { added, changed })
        {
            var problem = await response.SrsProblemAsync(HttpStatusCode.BadRequest, "MSG-GEN-01", "MSG01");
            Assert.Equal("Hourly rate", problem.Str("field"));
        }
    }

    [SqlServerTheory]
    [InlineData("79228162514264337593543950336")] // past decimal
    [InlineData("1e29")]
    [InlineData("1e400")]
    [InlineData("\"abc\"")]
    [InlineData("true")]
    [InlineData("[]")]
    [InlineData("{}")]
    public async Task An_amount_that_cannot_be_read_is_msg_gen_01_never_500(string json)
    {
        var jobType = await AddJobTypeAsync(sql.Owner);

        var added = await AddRawAsync(json);
        var changed = await RateRawAsync(jobType, json);

        await added.SrsProblemAsync(HttpStatusCode.BadRequest, "MSG-GEN-01", "MSG01");
        await changed.SrsProblemAsync(HttpStatusCode.BadRequest, "MSG-GEN-01", "MSG01");
    }

    // The JSON setting of the API reads numbers in text everywhere (JsonSerializerDefaults.Web): whole VND all the same, and "40000.5" still refused.
    [SqlServerFact]
    public async Task An_amount_sent_as_text_goes_by_the_same_rules()
    {
        Assert.Equal(HttpStatusCode.Created, (await AddRawAsync("\"40000\"")).StatusCode);
        await (await AddRawAsync("\"40000.5\"")).SrsProblemAsync(HttpStatusCode.BadRequest, "MSG-GEN-01", "MSG01");
        await (await AddRawAsync("\"0\"")).SrsProblemAsync(HttpStatusCode.BadRequest, "MSG-GEN-01", "MSG01");
    }

    [SqlServerTheory]
    [InlineData("\"2026-02-30\"")]
    [InlineData("\"2026-2-1\"")]
    [InlineData("\"2026-11-05T00:00:00\"")]
    [InlineData("\"05/11/2026\"")]
    [InlineData("\"\"")]
    [InlineData("20261105")]
    public async Task A_date_that_cannot_be_read_is_msg_gen_01_never_500(string json)
    {
        var jobType = await AddJobTypeAsync(sql.Owner);

        // The client list, section 5: such a value names the field "request", as the rest of the API does.
        Assert.Equal("request", (await (await AddRawAsync("40000", json)).SrsProblemAsync(HttpStatusCode.BadRequest, "MSG-GEN-01", "MSG01")).Str("field"));
        Assert.Equal("request", (await (await RateRawAsync(jobType, "40000", json)).SrsProblemAsync(HttpStatusCode.BadRequest, "MSG-GEN-01", "MSG01")).Str("field"));
    }

    [SqlServerTheory]
    [InlineData("0001-01-01")]
    [InlineData("9999-12-31")]
    public async Task The_first_and_the_last_date_of_the_column_never_answer_500(string date)
    {
        var jobType = await AddJobTypeAsync(sql.Owner, from: date);

        var view = await sql.Owner.ReadJobTypeAsync(jobType.IdOf());

        Assert.Equal([(40_000m, date)], RatesOf(view));
    }

    [SqlServerFact]
    public async Task A_new_rate_and_a_replaced_rate_rewrite_no_other_date_and_the_audit_holds_before_and_after()
    {
        var api = sql.Api;
        var (accountantClient, accountant) = await LoginNewAsync(api, OMTS.Domain.Enums.StaffRole.Accountant);
        var jobType = await AddJobTypeAsync(sql.Owner, rate: 38_000, from: "2020-01-01");
        var id = jobType.IdOf();
        var first = await QueryAsync(api, db => db.PayRates.AsNoTracking().SingleAsync(r => r.JobTypeId == id));

        var withSecond = (await (await sql.Owner.PostRateAsync(jobType, 40_000, "2021-01-01")).BodyAsync(HttpStatusCode.OK)).GetProperty("jobType");
        var second = await QueryAsync(api, db => db.PayRates.AsNoTracking().SingleAsync(r => r.JobTypeId == id && r.EffectiveFrom == new DateOnly(2021, 1, 1)));
        var withThird = (await (await sql.Owner.PostRateAsync(withSecond, 41_000, "2022-01-01")).BodyAsync(HttpStatusCode.OK)).GetProperty("jobType");
        var third = await QueryAsync(api, db => db.PayRates.AsNoTracking().SingleAsync(r => r.JobTypeId == id && r.EffectiveFrom == new DateOnly(2022, 1, 1)));

        var replaced = await accountantClient.PostRateAsync(withThird, 42_000, "2021-01-01");
        await replaced.BodyAsync(HttpStatusCode.OK);

        var rows = await QueryAsync(api, db => db.PayRates.AsNoTracking().Where(r => r.JobTypeId == id).OrderBy(r => r.EffectiveFrom).ToListAsync());
        Assert.Equal(3, rows.Count);
        // The dates not named keep their rows to the byte.
        Assert.Equal((first.Id, first.HourlyRate, Convert.ToBase64String(first.RowVersion), first.UpdatedAt, first.EnteredByAccountId), (rows[0].Id, rows[0].HourlyRate, Convert.ToBase64String(rows[0].RowVersion), rows[0].UpdatedAt, rows[0].EnteredByAccountId));
        Assert.Equal((third.Id, third.HourlyRate, Convert.ToBase64String(third.RowVersion), third.UpdatedAt), (rows[2].Id, rows[2].HourlyRate, Convert.ToBase64String(rows[2].RowVersion), rows[2].UpdatedAt));
        // The date named keeps its row, with the new amount and who entered it.
        Assert.Equal((second.Id, 42_000m, accountant.Id), (rows[1].Id, rows[1].HourlyRate, rows[1].EnteredByAccountId));

        var audit = await QueryAsync(api, db => db.AuditEvents.AsNoTracking().Where(e => e.RecordId == id).OrderBy(e => e.OccurredAt).ToListAsync());
        Assert.Equal(["JobType.Created", "JobType.RateChanged", "JobType.RateChanged", "JobType.RateChanged"], audit.Select(e => e.Action));
        Assert.All(audit, e => Assert.Equal((nameof(JobType), jobType.Str("name")), (e.RecordType, e.RecordCode)));
        Assert.Contains("38000", audit[0].After);
        Assert.Null(audit[1].Before);
        Assert.Contains("40000", audit[1].After);
        Assert.Contains("2021-01-01", audit[1].After);
        var replacement = audit[3];
        Assert.Equal(accountant.Id, replacement.ActorAccountId);
        Assert.Contains("40000", replacement.Before);
        Assert.Contains("2021-01-01", replacement.Before);
        Assert.Contains("42000", replacement.After);
    }

    [SqlServerFact]
    public async Task The_same_amount_on_the_same_date_moves_no_version_touches_no_draft_and_writes_no_audit_row()
    {
        var jobType = await AddJobTypeAsync(sql.Owner, rate: 38_000, from: "2030-01-15");
        var payroll = new Payroll { PeriodStart = new(2031, 1, 1), PeriodEnd = new(2031, 1, 31) };
        await ChangeAsync(sql.Api, db => db.Payrolls.Add(payroll));
        var audits = await QueryAsync(sql.Api, db => db.AuditEvents.CountAsync(e => e.RecordId == jobType.IdOf()));

        var saved = await (await sql.Owner.PostRateAsync(jobType, 38_000, "2030-01-15")).BodyAsync(HttpStatusCode.OK);

        Assert.Equal(jobType.VersionOf(), saved.GetProperty("jobType").VersionOf());
        Assert.Equal(audits, await QueryAsync(sql.Api, db => db.AuditEvents.CountAsync(e => e.RecordId == jobType.IdOf())));
        Assert.Null(await QueryAsync(sql.Api, db => db.Payrolls.Where(p => p.Id == payroll.Id).Select(p => p.UpdatedAt).SingleAsync()));
        await ChangeAsync(sql.Api, db => db.Payrolls.Remove(db.Payrolls.Single(p => p.Id == payroll.Id)));
    }

    [SqlServerFact]
    public async Task The_same_amount_on_another_date_and_on_another_job_type_are_rows_of_their_own()
    {
        var one = await AddJobTypeAsync(sql.Owner, rate: 38_000, from: "2020-01-01");
        var two = await AddJobTypeAsync(sql.Owner, rate: 38_000, from: "2020-01-01");

        var again = (await (await sql.Owner.PostRateAsync(one, 45_000, "2021-01-01")).BodyAsync(HttpStatusCode.OK)).GetProperty("jobType");
        var back = (await (await sql.Owner.PostRateAsync(again, 38_000, "2022-01-01")).BodyAsync(HttpStatusCode.OK)).GetProperty("jobType");

        Assert.Equal([(38_000m, "2022-01-01"), (45_000m, "2021-01-01"), (38_000m, "2020-01-01")], RatesOf(back));
        Assert.Equal([(38_000m, "2020-01-01")], RatesOf(await sql.Owner.ReadJobTypeAsync(two.IdOf())));
    }
}

/// <summary>QA of OMTS-227: which day is today (UTC+7, BusinessClock) for the current rate, the current settings and the next periods, at the midnight edge.</summary>
public class LaborSettingsQaBusinessDateTests
{
    private static readonly DateTimeOffset LastSecondOfNov5 = new(2026, 11, 5, 16, 59, 59, TimeSpan.Zero);
    private static readonly DateTimeOffset FirstSecondOfNov6 = new(2026, 11, 5, 17, 0, 0, TimeSpan.Zero);

    private sealed class Host : IAsyncDisposable
    {
        private readonly AuthApiFactory _factory = new();

        public Host()
        {
            App = _factory.WithWebHostBuilder(builder => builder.ConfigureTestServices(services =>
            {
                services.RemoveAll<TimeProvider>();
                services.AddSingleton<TimeProvider>(Clock);
            }));
        }

        public SettableClock Clock { get; } = new(LastSecondOfNov5);

        public Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactory<Program> App { get; }

        public async Task SeedAsync(Action<OMTS.Infrastructure.Persistence.OmtsDbContext> seed)
        {
            using var scope = App.Services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<OMTS.Infrastructure.Persistence.OmtsDbContext>();
            if (!await db.Cooperatives.AnyAsync())
            {
                db.Cooperatives.Add(new OMTS.Domain.Entities.Identity.Cooperative { FullName = "HTX" });
            }

            seed(db);
            await db.SaveChangesAsync();
        }

        public async Task<T> UseAsync<TService, T>(Func<TService, Task<T>> call) where TService : notnull
        {
            using var scope = App.Services.CreateScope();
            return await call(scope.ServiceProvider.GetRequiredService<TService>());
        }

        public async ValueTask DisposeAsync()
        {
            await _factory.DisposeAsync();
        }
    }

    [Fact]
    public async Task The_current_rate_changes_at_midnight_in_utc_plus_7()
    {
        await using var host = new Host();
        var jobType = new JobType { Name = "Thu hoạch" };
        jobType.Rates.Add(new PayRate { HourlyRate = 38_000, EffectiveFrom = new(2026, 1, 1), EnteredByAccountId = Guid.NewGuid() });
        jobType.Rates.Add(new PayRate { HourlyRate = 40_000, EffectiveFrom = new(2026, 11, 6), EnteredByAccountId = Guid.NewGuid() });
        jobType.Rates.Add(new PayRate { HourlyRate = 42_000, EffectiveFrom = new(2026, 11, 7), EnteredByAccountId = Guid.NewGuid() });
        await host.SeedAsync(db => db.JobTypes.Add(jobType));

        async Task<JobTypeView> ReadAsync() => (await host.UseAsync<JobTypeQueryService, OMTS.Application.Common.Results.Result<JobTypeView>>(s => s.GetAsync(jobType.Id, default))).Value!;

        host.Clock.Now = new(2025, 12, 31, 16, 59, 59, TimeSpan.Zero); // before the first rate
        var before = await ReadAsync();
        Assert.Null(before.CurrentRate);
        Assert.Equal([new DateOnly(2026, 11, 7), new DateOnly(2026, 11, 6), new DateOnly(2026, 1, 1)], before.OtherRates.Select(r => r.EffectiveFrom));

        host.Clock.Now = new(2025, 12, 31, 17, 0, 0, TimeSpan.Zero); // on it
        Assert.Equal(38_000m, (await ReadAsync()).CurrentRate!.HourlyRate);

        host.Clock.Now = LastSecondOfNov5;
        Assert.Equal(38_000m, (await ReadAsync()).CurrentRate!.HourlyRate);

        host.Clock.Now = FirstSecondOfNov6;
        var onIt = await ReadAsync();
        Assert.Equal((40_000m, new DateOnly(2026, 11, 6)), (onIt.CurrentRate!.HourlyRate, onIt.CurrentRate.EffectiveFrom));
        Assert.Equal([new DateOnly(2026, 11, 7), new DateOnly(2026, 1, 1)], onIt.OtherRates.Select(r => r.EffectiveFrom));
    }

    [Fact]
    public async Task The_current_settings_change_at_midnight_in_utc_plus_7()
    {
        await using var host = new Host();
        await host.SeedAsync(db => db.AttendanceSettingsVersions.Add(new AttendanceSettingsVersion
        {
            EffectiveFrom = new(2026, 11, 6), RoundingUnit = OMTS.Domain.Enums.AttendanceRoundingUnit.Minutes15, EnteredByAccountId = Guid.NewGuid(),
        }));

        async Task<AttendanceSettingsView> CurrentAsync() =>
            (await host.UseAsync<AttendanceSettingsService, OMTS.Application.Common.Results.Result<AttendanceSettingsOverview>>(s => s.GetAsync(default))).Value!.Current;

        host.Clock.Now = LastSecondOfNov5;
        Assert.Null((await CurrentAsync()).EffectiveFrom);

        host.Clock.Now = FirstSecondOfNov6;
        Assert.Equal(OMTS.Domain.Enums.AttendanceRoundingUnit.Minutes15, (await CurrentAsync()).RoundingUnit);
    }

    [Theory]
    [InlineData("2026-10-31T16:59:59Z", 1, "2026-10-01", "2026-10-31", "10/2026")]
    [InlineData("2026-10-31T17:00:00Z", 1, "2026-11-01", "2026-11-30", "11/2026")]
    [InlineData("2026-11-25T16:59:59Z", 26, "2026-10-26", "2026-11-25", "11/2026")]
    [InlineData("2026-11-25T17:00:00Z", 26, "2026-11-26", "2026-12-25", "12/2026")]
    [InlineData("2026-12-31T16:59:59Z", 1, "2026-12-01", "2026-12-31", "12/2026")]
    [InlineData("2026-12-31T17:00:00Z", 1, "2027-01-01", "2027-01-31", "01/2027")]
    [InlineData("2028-02-28T17:00:00Z", 1, "2028-02-01", "2028-02-29", "02/2028")]
    public async Task With_no_payroll_the_first_next_period_holds_today_in_utc_plus_7(string now, int startDay, string start, string end, string name)
    {
        await using var host = new Host();
        await host.SeedAsync(_ => { });
        host.Clock.Now = DateTimeOffset.Parse(now);

        var preview = (await host.UseAsync<PayrollSettingsService, OMTS.Application.Common.Results.Result<PayrollSettingsPreview>>(s => s.PreviewAsync(startDay, null, default))).Value!;

        Assert.Equal(new PayPeriod(DateOnly.Parse(start), DateOnly.Parse(end)), preview.NextPeriods[0]);
        Assert.Equal(name, preview.NextPeriods[0].Name);
    }
}
