using System.Net;
using System.Net.Http.Json;
using OMTS.Api.IntegrationTests.Infrastructure;
using OMTS.Domain.Enums;
using static OMTS.Api.IntegrationTests.Infrastructure.AcsTestKit;
using static OMTS.Api.IntegrationTests.Labor.LaborSettingsTestKit;

namespace OMTS.Api.IntegrationTests.Labor;

/// <summary>
/// The §I.4.4 matrix: LABOR-11 is Owner and Accountant; LABOR-15 and LABOR-16 are the Owner only. The job type names to choose
/// from go with LABOR-01: Owner and Zone Manager.
/// </summary>
public class LaborSettingsAccessTests(AuthApiFactory factory) : IClassFixture<AuthApiFactory>
{
    private static async Task<HttpResponseMessage[]> AllJobTypeCallsAsync(HttpClient client, Guid id)
    {
        var path = $"{JobTypes}/{id}";
        return
        [
            await client.GetAsync(JobTypes),
            await client.GetAsync(path),
            await client.PostAsJsonAsync(JobTypes, new { name = NewCode(), hourlyRate = 1, effectiveFrom = "2020-01-01" }),
            await client.PostAsJsonAsync($"{path}/rates", new { hourlyRate = 1, effectiveFrom = "2020-01-01", rowVersion = "" }),
            await client.PutAsJsonAsync($"{path}/name", new { name = NewCode(), rowVersion = "" }),
            await client.PostAsJsonAsync($"{path}/discontinue", new { rowVersion = "" }),
            await client.PostAsJsonAsync($"{path}/resume", new { rowVersion = "" }),
        ];
    }

    private static async Task<HttpResponseMessage[]> AllSettingsCallsAsync(HttpClient client) =>
    [
        await client.GetAsync(AttendanceSettings),
        await client.PostAsJsonAsync(AttendanceSettings, new { roundingUnit = "None", effectiveFrom = "2020-01-01", rowVersion = "" }),
        await client.GetAsync(PayrollSettings),
        await client.GetAsync($"{PayrollSettings}/preview?startDay=26"),
        await client.PutAsJsonAsync(PayrollSettings, new { payPeriodStartDay = 26, automaticPayroll = true, rowVersion = "" }),
    ];

    private static async Task AssertAllForbiddenAsync(IEnumerable<HttpResponseMessage> responses)
    {
        foreach (var response in responses)
        {
            await response.SrsProblemAsync(HttpStatusCode.Forbidden, "MSG-GEN-08", "MSG02");
        }
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task An_accountant_of_any_pair_manages_job_types(bool allZones)
    {
        var accountant = await StaffAsync(factory, StaffRole.Accountant, allZones);

        var jobType = await AddJobTypeAsync(accountant);
        var path = $"{JobTypes}/{jobType.IdOf()}";

        Assert.Equal(HttpStatusCode.OK, (await accountant.GetAsync(JobTypes)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await accountant.GetAsync(path)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await accountant.PostRateAsync(jobType, 50_000, "2021-01-01")).StatusCode);
        var current = await accountant.ReadJobTypeAsync(jobType.IdOf());
        Assert.Equal(HttpStatusCode.OK, (await accountant.PostAsJsonAsync($"{path}/discontinue", new { rowVersion = current.VersionOf() })).StatusCode);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task A_zone_manager_has_no_rates_but_reads_the_names_to_choose_from(bool allZones)
    {
        var owner = await OwnerAsync(factory);
        var jobType = await AddJobTypeAsync(owner);
        var manager = await StaffAsync(factory, StaffRole.ZoneManager, allZones);

        await AssertAllForbiddenAsync(await AllJobTypeCallsAsync(manager, jobType.IdOf()));

        Assert.Equal(HttpStatusCode.OK, (await manager.GetAsync($"{JobTypes}/options")).StatusCode);
    }

    [Fact]
    public async Task An_accountant_does_not_use_the_names_of_labor_01()
    {
        var accountant = await StaffAsync(factory, StaffRole.Accountant);

        await (await accountant.GetAsync($"{JobTypes}/options")).SrsProblemAsync(HttpStatusCode.Forbidden, "MSG-GEN-08", "MSG02");
    }

    [Fact]
    public async Task A_trader_gets_nothing()
    {
        await factory.EnsureCooperativeAsync();
        var password = NewPassword();
        var account = await factory.AddAccountAsync(NewCode(), password, SubjectType.Trader);
        var trader = await factory.LoginAsync(account.Username, password);

        await AssertAllForbiddenAsync(await AllJobTypeCallsAsync(trader, Guid.NewGuid()));
        await AssertAllForbiddenAsync(await AllSettingsCallsAsync(trader));
        await AssertAllForbiddenAsync([await trader.GetAsync($"{JobTypes}/options")]);
    }

    [Theory]
    [InlineData(StaffRole.ZoneManager, true)]
    [InlineData(StaffRole.ZoneManager, false)]
    [InlineData(StaffRole.Accountant, true)]
    [InlineData(StaffRole.Accountant, false)]
    public async Task The_attendance_and_payroll_settings_are_the_owners_only(StaffRole role, bool allZones)
    {
        var staff = await StaffAsync(factory, role, allZones);

        await AssertAllForbiddenAsync(await AllSettingsCallsAsync(staff));
    }

    [Fact]
    public async Task Without_a_token_every_route_answers_401()
    {
        var anonymous = factory.CreateClient();

        var responses = (await AllJobTypeCallsAsync(anonymous, Guid.NewGuid())).Concat(await AllSettingsCallsAsync(anonymous)).Append(await anonymous.GetAsync($"{JobTypes}/options"));

        Assert.All(responses, r => Assert.Equal(HttpStatusCode.Unauthorized, r.StatusCode));
    }
}
