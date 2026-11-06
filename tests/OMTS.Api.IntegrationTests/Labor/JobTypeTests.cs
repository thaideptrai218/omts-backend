using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using OMTS.Api.IntegrationTests.Employees;
using OMTS.Api.IntegrationTests.Infrastructure;
using OMTS.Domain.Constants;
using static OMTS.Api.IntegrationTests.Infrastructure.AcsTestKit;
using static OMTS.Api.IntegrationTests.Labor.LaborSettingsTestKit;

namespace OMTS.Api.IntegrationTests.Labor;

/// <summary>LABOR-11: add, rename, discontinue and resume job types, and the rates read by their effective dates. No payroll in this class.</summary>
public class JobTypeTests(AuthApiFactory factory) : IClassFixture<AuthApiFactory>
{
    [Fact]
    public async Task A_new_job_type_is_in_use_with_its_first_rate_and_written_to_the_audit_log()
    {
        var owner = await OwnerAsync(factory);
        var name = $"Thu hoạch {NewCode()}";
        var before = await factory.AuditCountAsync("JobType.Created");

        var response = await owner.PostAsJsonAsync(JobTypes, new { name = $"  {name} ", hourlyRate = 40000.0m, effectiveFrom = "2020-01-01" });

        var saved = await response.BodyAsync(HttpStatusCode.Created);
        Assert.Equal(("MSG-GEN-05", "Job type saved."), (saved.Str("messageCode"), saved.Str("message")));
        var jobType = saved.GetProperty("jobType");
        Assert.Equal((name, "In Use", StatusIds.Usage.InUse), (jobType.Str("name"), jobType.Str("status"), jobType.GetProperty("statusId").GetInt32()));
        Assert.Equal((40000m, "2020-01-01"), RateOf(jobType.GetProperty("currentRate")));
        Assert.Equal("40000", jobType.GetProperty("currentRate").GetProperty("hourlyRate").GetRawText());
        Assert.Empty(jobType.GetProperty("otherRates").EnumerateArray());
        Assert.Equal(before + 1, await factory.AuditCountAsync("JobType.Created"));
    }

    [Fact]
    public async Task Every_missing_field_of_a_new_job_type_is_listed()
    {
        var owner = await OwnerAsync(factory);

        var response = await owner.PostAsJsonAsync(JobTypes, new { name = "  " });

        var problem = await response.SrsProblemAsync(HttpStatusCode.BadRequest, "MSG-GEN-01", "MSG01");
        Assert.Equal(["Name", "Hourly rate", "Effective date"], problem.GetProperty("fields").EnumerateArray().Select(f => f.GetString()));
    }

    [Fact]
    public async Task A_name_of_101_characters_is_refused_and_one_of_100_fits()
    {
        var owner = await OwnerAsync(factory);
        var longest = NewCode().PadRight(100, 'x');

        var refused = await owner.PostAsJsonAsync(JobTypes, new { name = longest + "x", hourlyRate = 1, effectiveFrom = "2020-01-01" });

        Assert.Equal("Name", await EmployeeRequests.FieldAsync(refused));
        Assert.Equal(longest, (await AddJobTypeAsync(owner, longest)).Str("name"));
    }

    [Fact]
    public async Task The_name_of_another_job_type_is_msg_gen_02_with_its_id_whatever_the_case()
    {
        var owner = await OwnerAsync(factory);
        var name = $"Treo giống {NewCode()}";
        var first = await AddJobTypeAsync(owner, name);
        var second = await AddJobTypeAsync(owner);

        var added = await owner.PostAsJsonAsync(JobTypes, new { name = name.ToUpperInvariant(), hourlyRate = 1, effectiveFrom = "2020-01-01" });
        var renamed = await owner.PutAsJsonAsync($"{JobTypes}/{second.IdOf()}/name", new { name, rowVersion = second.VersionOf() });

        var addedProblem = await added.SrsProblemAsync(HttpStatusCode.Conflict, "MSG-GEN-02", "MSG03");
        var renamedProblem = await renamed.SrsProblemAsync(HttpStatusCode.Conflict, "MSG-GEN-02", "MSG03");
        Assert.StartsWith("Job type already exists.", addedProblem.Detail());
        Assert.Equal(first.IdOf(), addedProblem.GetProperty("existingId").GetGuid());
        Assert.Equal(first.IdOf(), renamedProblem.GetProperty("existingId").GetGuid());
    }

    [Fact]
    public async Task A_discontinued_job_type_keeps_its_rates_takes_no_new_rate_and_can_be_resumed_and_renamed()
    {
        var owner = await OwnerAsync(factory);
        var jobType = await AddJobTypeAsync(owner);

        var discontinued = (await (await owner.PostAsJsonAsync($"{JobTypes}/{jobType.IdOf()}/discontinue", new { rowVersion = jobType.VersionOf() })).BodyAsync(HttpStatusCode.OK)).GetProperty("jobType");

        Assert.Equal("Discontinued", discontinued.Str("status"));
        Assert.Single(RatesOf(discontinued));
        var refused = await owner.PostRateAsync(discontinued, 50_000, "2022-01-01");
        var again = await owner.PostAsJsonAsync($"{JobTypes}/{jobType.IdOf()}/discontinue", new { rowVersion = discontinued.VersionOf() });
        var refusedProblem = await refused.SrsProblemAsync(HttpStatusCode.Conflict, "MSG-GEN-09", "MSG61");
        Assert.Equal($"Job type {jobType.Str("name")} is Discontinued and cannot be used for this action.", refusedProblem.Detail());
        Assert.Equal("MSG-GEN-09", await again.ErrorCodeAsync());

        var newName = $"Đã đổi {NewCode()}";
        var renamed = (await (await owner.PutAsJsonAsync($"{JobTypes}/{jobType.IdOf()}/name", new { name = newName, rowVersion = discontinued.VersionOf() })).BodyAsync(HttpStatusCode.OK)).GetProperty("jobType");
        Assert.Equal((newName, "Discontinued"), (renamed.Str("name"), renamed.Str("status")));

        var resumed = (await (await owner.PostAsJsonAsync($"{JobTypes}/{jobType.IdOf()}/resume", new { rowVersion = renamed.VersionOf() })).BodyAsync(HttpStatusCode.OK)).GetProperty("jobType");
        Assert.Equal("In Use", resumed.Str("status"));
        var inUse = await owner.PostAsJsonAsync($"{JobTypes}/{jobType.IdOf()}/resume", new { rowVersion = resumed.VersionOf() });
        Assert.Equal("MSG-GEN-09", await inUse.ErrorCodeAsync());
        Assert.Equal(2, await factory.QueryAsync(db => db.AuditEvents.CountAsync(e => e.RecordId == jobType.IdOf() && e.Action == "JobType.StatusChanged")));
    }

    [Fact]
    public async Task A_rename_keeps_the_rates_and_the_same_name_writes_no_audit_row()
    {
        var owner = await OwnerAsync(factory);
        var jobType = await AddJobTypeAsync(owner);
        var name = $"Mới {NewCode()}";

        var renamed = (await (await owner.PutAsJsonAsync($"{JobTypes}/{jobType.IdOf()}/name", new { name, rowVersion = jobType.VersionOf() })).BodyAsync(HttpStatusCode.OK)).GetProperty("jobType");
        var count = await factory.QueryAsync(db => db.AuditEvents.CountAsync(e => e.RecordId == jobType.IdOf() && e.Action == "JobType.Renamed"));
        var same = await owner.PutAsJsonAsync($"{JobTypes}/{jobType.IdOf()}/name", new { name, rowVersion = renamed.VersionOf() });

        Assert.Equal(HttpStatusCode.OK, same.StatusCode);
        Assert.Equal(RatesOf(jobType), RatesOf(renamed));
        Assert.Equal(1, count);
        Assert.Equal(1, await factory.QueryAsync(db => db.AuditEvents.CountAsync(e => e.RecordId == jobType.IdOf() && e.Action == "JobType.Renamed")));
    }

    [Fact]
    public async Task A_change_without_a_row_version_is_msg_gen_01()
    {
        var owner = await OwnerAsync(factory);
        var jobType = await AddJobTypeAsync(owner);
        var path = $"{JobTypes}/{jobType.IdOf()}";

        var responses = new[]
        {
            await owner.PostAsJsonAsync($"{path}/rates", new { hourlyRate = 1, effectiveFrom = "2022-01-01" }),
            await owner.PutAsJsonAsync($"{path}/name", new { name = NewCode() }),
            await owner.PostAsJsonAsync($"{path}/discontinue", new { }),
        };

        foreach (var response in responses)
        {
            Assert.Equal(("MSG-GEN-01", "RowVersion"), (await response.ErrorCodeAsync(), await EmployeeRequests.FieldAsync(response)));
        }
    }

    [Fact]
    public async Task An_unknown_job_type_answers_404_whatever_is_sent()
    {
        var owner = await OwnerAsync(factory);
        var path = $"{JobTypes}/{Guid.NewGuid()}";

        var responses = new[]
        {
            await owner.GetAsync(path),
            await owner.PostAsJsonAsync($"{path}/rates", new { hourlyRate = -1 }),
            await owner.PutAsJsonAsync($"{path}/name", new { name = "" }),
            await owner.PostAsJsonAsync($"{path}/discontinue", new { }),
            await owner.PostAsJsonAsync($"{path}/resume", new { }),
        };

        Assert.All(responses, r => Assert.Equal(HttpStatusCode.NotFound, r.StatusCode));
    }

    [Fact]
    public async Task The_list_finds_by_name_and_status_and_bounds_its_paging()
    {
        var owner = await OwnerAsync(factory);
        var tag = NewCode();
        var one = await AddJobTypeAsync(owner, $"{tag} một");
        var two = await AddJobTypeAsync(owner, $"{tag} hai");
        await owner.PostAsJsonAsync($"{JobTypes}/{two.IdOf()}/discontinue", new { rowVersion = two.VersionOf() });

        var found = await owner.GetFromJsonAsync<JsonElement>($"{JobTypes}?search={tag}");
        var inUse = await owner.GetFromJsonAsync<JsonElement>($"{JobTypes}?search={tag}&statusId={StatusIds.Usage.InUse}");
        var tooLong = await owner.GetFromJsonAsync<JsonElement>($"{JobTypes}?search={new string('x', 101)}");
        var farPage = await owner.GetFromJsonAsync<JsonElement>($"{JobTypes}?page={int.MaxValue}&pageSize=100000");

        Assert.Equal(2, found.GetProperty("total").GetInt32());
        Assert.Equal(one.IdOf(), inUse.GetProperty("items")[0].IdOf());
        Assert.Equal(1, inUse.GetProperty("total").GetInt32());
        Assert.Equal(0, tooLong.GetProperty("total").GetInt32());
        Assert.Equal(100, farPage.GetProperty("pageSize").GetInt32());
        Assert.Empty(farPage.GetProperty("items").EnumerateArray());
    }

    [Fact]
    public async Task The_options_hold_the_job_types_in_use_without_rates_and_a_profile_can_choose_them()
    {
        var owner = await OwnerAsync(factory);
        var (kept, gone) = (await AddJobTypeAsync(owner), await AddJobTypeAsync(owner));
        await owner.PostAsJsonAsync($"{JobTypes}/{gone.IdOf()}/discontinue", new { rowVersion = gone.VersionOf() });

        var options = await owner.GetFromJsonAsync<JsonElement>($"{JobTypes}/options");

        var option = options.EnumerateArray().Single(o => o.IdOf() == kept.IdOf());
        Assert.Equal(["id", "name"], option.EnumerateObject().Select(p => p.Name));
        Assert.DoesNotContain(options.EnumerateArray(), o => o.IdOf() == gone.IdOf());

        var zone = await factory.AddZoneAsync(NewCode());
        var saved = await EmployeeRequests.CreateAsync(owner, EmployeeRequests.Profile($"Có việc {NewCode()}", NewPhone(), zone.Id, b => b["jobTypeIds"] = new[] { kept.IdOf() }));
        Assert.Equal(kept.IdOf(), saved.GetProperty("employee").GetProperty("jobTypes")[0].IdOf());

        var refused = await owner.PostAsJsonAsync("/api/employees", EmployeeRequests.Profile($"Không {NewCode()}", NewPhone(), zone.Id, b => b["jobTypeIds"] = new[] { gone.IdOf() }));
        Assert.Equal("Job types", await EmployeeRequests.FieldAsync(refused));
    }

    [Fact]
    public async Task A_new_name_shows_on_the_profile_that_has_the_job_type()
    {
        var owner = await OwnerAsync(factory);
        var jobType = await AddJobTypeAsync(owner);
        var zone = await factory.AddZoneAsync(NewCode());
        var saved = await EmployeeRequests.CreateAsync(owner, EmployeeRequests.Profile($"Hồ sơ {NewCode()}", NewPhone(), zone.Id, b => b["jobTypeIds"] = new[] { jobType.IdOf() }));
        var name = $"Tên mới {NewCode()}";

        await owner.PutAsJsonAsync($"{JobTypes}/{jobType.IdOf()}/name", new { name, rowVersion = jobType.VersionOf() });

        var view = await owner.GetFromJsonAsync<JsonElement>($"/api/employees/{EmployeeRequests.IdOf(saved)}");
        Assert.Equal(name, view.GetProperty("jobTypes")[0].Str("name"));
    }
}
