using System.Net;
using System.Text.Json;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using OMTS.Api.IntegrationTests.Employees;
using OMTS.Api.IntegrationTests.Infrastructure;
using static OMTS.Api.IntegrationTests.Infrastructure.AcsTestKit;
using static OMTS.Api.IntegrationTests.Labor.LaborSettingsTestKit;

namespace OMTS.Api.IntegrationTests.Labor;

/// <summary>LABOR-11.0, BR-LABOR-12: rates in whole VND, read by their effective dates, replaced on the same date. No payroll in this class.</summary>
public class PayRateTests(AuthApiFactory factory) : IClassFixture<AuthApiFactory>
{
    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(40000.5)]
    [InlineData(1000000000)]
    public async Task A_rate_that_is_not_whole_vnd_above_0_is_refused(double rate)
    {
        var owner = await OwnerAsync(factory);
        var jobType = await AddJobTypeAsync(owner);

        var added = await owner.PostAsJsonAsync(JobTypes, new { name = NewCode(), hourlyRate = rate, effectiveFrom = "2020-01-01" });
        var changed = await owner.PostRateAsync(jobType, (decimal)rate, "2021-01-01");

        Assert.Equal(("MSG-GEN-01", "Hourly rate"), (await added.ErrorCodeAsync(), await EmployeeRequests.FieldAsync(added)));
        Assert.Equal(("MSG-GEN-01", "Hourly rate"), (await changed.ErrorCodeAsync(), await EmployeeRequests.FieldAsync(changed)));
        Assert.Single(RatesOf(await owner.ReadJobTypeAsync(jobType.IdOf())));
    }

    [Fact]
    public async Task A_rate_is_read_by_its_effective_date_so_a_new_one_does_not_rewrite_the_current_one()
    {
        var owner = await OwnerAsync(factory);
        var jobType = await AddJobTypeAsync(owner, rate: 38_000, from: "2020-01-01");

        var future = await (await owner.PostRateAsync(jobType, 90_000, "2099-01-01")).BodyAsync(HttpStatusCode.OK);
        var withFuture = future.GetProperty("jobType");

        // A rate still to come is not the current one.
        Assert.Equal((38_000m, "2020-01-01"), RateOf(withFuture.GetProperty("currentRate")));
        Assert.Equal([(90_000m, "2099-01-01")], withFuture.GetProperty("otherRates").EnumerateArray().Select(RateOf));

        var past = await (await owner.PostRateAsync(withFuture, 40_000, "2021-06-15")).BodyAsync(HttpStatusCode.OK);
        var view = past.GetProperty("jobType");

        // A later date up to today is; the earlier rate stays, latest first among the others.
        Assert.Equal((40_000m, "2021-06-15"), RateOf(view.GetProperty("currentRate")));
        Assert.Equal([(90_000m, "2099-01-01"), (38_000m, "2020-01-01")], view.GetProperty("otherRates").EnumerateArray().Select(RateOf));
    }

    [Fact]
    public async Task A_job_type_whose_only_rate_is_still_to_come_has_no_current_rate()
    {
        var owner = await OwnerAsync(factory);

        var jobType = await AddJobTypeAsync(owner, from: "2099-03-01");

        Assert.Equal(JsonValueKind.Null, jobType.GetProperty("currentRate").ValueKind);
        Assert.Equal([(40_000m, "2099-03-01")], jobType.GetProperty("otherRates").EnumerateArray().Select(RateOf));
    }

    [Fact]
    public async Task A_new_rate_on_the_same_date_replaces_it_keeps_the_others_and_the_audit_keeps_the_old_one()
    {
        var owner = await OwnerAsync(factory);
        var jobType = await AddJobTypeAsync(owner, rate: 38_000, from: "2020-01-01");
        var second = (await (await owner.PostRateAsync(jobType, 40_000, "2021-01-01")).BodyAsync(HttpStatusCode.OK)).GetProperty("jobType");
        var replacedBefore = await factory.AuditCountAsync("JobType.RateChanged");

        var saved = await (await owner.PostRateAsync(second, 42_000, "2021-01-01")).BodyAsync(HttpStatusCode.OK);

        Assert.Equal([(42_000m, "2021-01-01"), (38_000m, "2020-01-01")], RatesOf(saved.GetProperty("jobType")));
        Assert.Equal(2, await factory.QueryAsync(db => db.PayRates.CountAsync(r => r.JobTypeId == jobType.IdOf())));
        Assert.Equal(replacedBefore + 1, await factory.AuditCountAsync("JobType.RateChanged"));
        Assert.Contains("40000", await factory.LastAuditBeforeAsync("JobType.RateChanged"));
    }

    [Fact]
    public async Task The_same_rate_on_the_same_date_changes_nothing_and_writes_no_audit_row()
    {
        var owner = await OwnerAsync(factory);
        var jobType = await AddJobTypeAsync(owner, rate: 38_000, from: "2020-01-01");
        var before = await factory.AuditCountAsync("JobType.RateChanged");

        var saved = await (await owner.PostRateAsync(jobType, 38_000, "2020-01-01")).BodyAsync(HttpStatusCode.OK);

        Assert.Equal([(38_000m, "2020-01-01")], RatesOf(saved.GetProperty("jobType")));
        Assert.Equal(before, await factory.AuditCountAsync("JobType.RateChanged"));
    }
}
