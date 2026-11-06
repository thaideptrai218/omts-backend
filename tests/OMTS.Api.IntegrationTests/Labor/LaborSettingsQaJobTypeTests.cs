using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using OMTS.Api.IntegrationTests.Employees;
using OMTS.Api.IntegrationTests.Infrastructure;
using OMTS.Domain.Constants;
using static OMTS.Api.IntegrationTests.Infrastructure.AcsTestKit;
using static OMTS.Api.IntegrationTests.Infrastructure.FarmLabSqlServerKit;
using static OMTS.Api.IntegrationTests.Labor.LaborSettingsTestKit;

namespace OMTS.Api.IntegrationTests.Labor;

/// <summary>QA of OMTS-227, BR-LABOR-12 "a name that no other job type has" on the real unique index of SQL Server; the search on it.</summary>
public class LaborSettingsQaJobTypeNameTests(LaborSqlFixture sql) : IClassFixture<LaborSqlFixture>
{
    private const string TwoWords = "Thu hoạch";

    private static string Variant(string kind, string name) => kind switch
    {
        "upper" => name.ToUpperInvariant(),
        "lower" => name.ToLowerInvariant(),
        "decomposed" => name.Normalize(NormalizationForm.FormD),
        "outer spaces" => $"  {name}\t ",
        "outer no-break space" => $" {name} ",
        "two inner spaces" => name.Replace(" ", "  "),
        "inner no-break space" => name.Replace(" ", " "),
        "inner tab" => name.Replace(" ", "\t"),
        "inner line break" => name.Replace(" ", "\n"),
        "zero width space" => name + "​",
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, null),
    };

    private async Task AssertTakenAsync(string kind)
    {
        var name = $"{NewCode()} {TwoWords}";
        var first = await AddJobTypeAsync(sql.Owner, name);
        var other = await AddJobTypeAsync(sql.Owner);
        var variant = Variant(kind, name);

        var added = await sql.Owner.PostAsJsonAsync(JobTypes, new { name = variant, hourlyRate = 1, effectiveFrom = "2020-01-01" });
        var renamed = await sql.Owner.PutAsJsonAsync($"{JobTypes}/{other.IdOf()}/name", new { name = variant, rowVersion = other.VersionOf() });

        var addedProblem = await added.SrsProblemAsync(HttpStatusCode.Conflict, "MSG-GEN-02", "MSG03");
        var renamedProblem = await renamed.SrsProblemAsync(HttpStatusCode.Conflict, "MSG-GEN-02", "MSG03");
        Assert.Equal(first.IdOf(), addedProblem.GetProperty("existingId").GetGuid());
        Assert.Equal(first.IdOf(), renamedProblem.GetProperty("existingId").GetGuid());
    }

    [SqlServerTheory]
    [InlineData("upper")]
    [InlineData("lower")]
    [InlineData("outer spaces")]
    [InlineData("outer no-break space")]
    public async Task A_name_that_reads_the_same_is_taken(string kind) => await AssertTakenAsync(kind);

    // M1. Same letters to the eye, and the first is the same text by Unicode: the collation of the index (here Latin1_General_CI_AS) calls them different.
    [SqlServerTheory]
    [InlineData("decomposed")]
    [InlineData("two inner spaces")]
    [InlineData("inner no-break space")]
    [InlineData("inner tab")]
    [InlineData("inner line break")]
    [InlineData("zero width space")]
    public async Task M1_a_name_that_reads_the_same_is_taken_whatever_its_unicode_form_or_spacing(string kind) => await AssertTakenAsync(kind);

    [SqlServerFact]
    public async Task The_letter_d_with_stroke_is_one_letter_in_either_case_and_not_a_plain_d()
    {
        var tag = NewCode();
        await AddJobTypeAsync(sql.Owner, $"{tag} Đóng gói");

        var upper = await sql.Owner.PostAsJsonAsync(JobTypes, new { name = $"{tag} đóng gói", hourlyRate = 1, effectiveFrom = "2020-01-01" });
        var plain = await sql.Owner.PostAsJsonAsync(JobTypes, new { name = $"{tag} Dóng gói", hourlyRate = 1, effectiveFrom = "2020-01-01" });

        Assert.Equal("MSG-GEN-02", await upper.ErrorCodeAsync());
        Assert.Equal(HttpStatusCode.Created, plain.StatusCode);
    }

    // Other words in Vietnamese, so another job type: not a defect. The tone on another letter is a PM call: see the questions of the QA report.
    [SqlServerFact]
    public async Task Another_diacritic_is_another_name()
    {
        var tag = NewCode();
        await AddJobTypeAsync(sql.Owner, $"{tag} Thu hoạch");

        var noMarks = await sql.Owner.PostAsJsonAsync(JobTypes, new { name = $"{tag} Thu hoach", hourlyRate = 1, effectiveFrom = "2020-01-01" });
        var tonePlacement = await sql.Owner.PostAsJsonAsync(JobTypes, new { name = $"{tag} Thu họach", hourlyRate = 1, effectiveFrom = "2020-01-01" });

        Assert.Equal(HttpStatusCode.Created, noMarks.StatusCode);
        Assert.Equal(HttpStatusCode.Created, tonePlacement.StatusCode);
    }

    [SqlServerFact]
    public async Task A_discontinued_job_type_keeps_its_name_taken()
    {
        var name = $"{NewCode()} Cũ";
        var old = await AddJobTypeAsync(sql.Owner, name);
        await (await sql.Owner.PostAsJsonAsync($"{JobTypes}/{old.IdOf()}/discontinue", new { rowVersion = old.VersionOf() })).BodyAsync(HttpStatusCode.OK);

        var added = await sql.Owner.PostAsJsonAsync(JobTypes, new { name, hourlyRate = 1, effectiveFrom = "2020-01-01" });

        Assert.Equal(old.IdOf(), (await added.SrsProblemAsync(HttpStatusCode.Conflict, "MSG-GEN-02", "MSG03")).GetProperty("existingId").GetGuid());
    }

    [SqlServerFact]
    public async Task A_rename_in_letter_case_only_is_saved_and_audited()
    {
        var name = $"{NewCode()} thu hoạch";
        var jobType = await AddJobTypeAsync(sql.Owner, name);

        var renamed = await sql.Owner.PutAsJsonAsync($"{JobTypes}/{jobType.IdOf()}/name", new { name = name.ToUpperInvariant(), rowVersion = jobType.VersionOf() });

        Assert.Equal(name.ToUpperInvariant(), (await renamed.BodyAsync(HttpStatusCode.OK)).GetProperty("jobType").Str("name"));
        var audit = await QueryAsync(sql.Api, db => db.AuditEvents.SingleAsync(e => e.RecordId == jobType.IdOf() && e.Action == "JobType.Renamed"));
        Assert.Equal(name, JsonDocument.Parse(audit.Before!).RootElement.GetProperty("Name").GetString());
        Assert.Equal(name.ToUpperInvariant(), JsonDocument.Parse(audit.After!).RootElement.GetProperty("Name").GetString());
    }

    // A name freed by a rename is another job type when used again: the old rates stay with the old one, none reach the new one.
    [SqlServerFact]
    public async Task A_name_used_again_by_a_later_job_type_takes_no_rate_of_the_earlier_one()
    {
        var name = $"{NewCode()} Dùng lại";
        var earlier = await AddJobTypeAsync(sql.Owner, name, 38_000, "2020-01-01");
        await sql.Owner.PutAsJsonAsync($"{JobTypes}/{earlier.IdOf()}/name", new { name = $"{name} cũ", rowVersion = earlier.VersionOf() });

        var later = await AddJobTypeAsync(sql.Owner, name, 50_000, "2021-01-01");

        Assert.NotEqual(earlier.IdOf(), later.IdOf());
        Assert.Equal([(50_000m, "2021-01-01")], RatesOf(await sql.Owner.ReadJobTypeAsync(later.IdOf())));
        Assert.Equal([(38_000m, "2020-01-01")], RatesOf(await sql.Owner.ReadJobTypeAsync(earlier.IdOf())));
    }

    [SqlServerFact]
    public async Task The_longest_name_of_vietnamese_letters_fits_the_column_and_one_more_is_refused()
    {
        var longest = (NewCode() + new string('ạ', 100))[..100];

        var saved = await AddJobTypeAsync(sql.Owner, longest);
        var refused = await sql.Owner.PostAsJsonAsync(JobTypes, new { name = longest + "ạ", hourlyRate = 1, effectiveFrom = "2020-01-01" });

        Assert.Equal(longest, saved.Str("name"));
        Assert.Equal("Name", await EmployeeRequests.FieldAsync(refused));
    }

    [SqlServerTheory]
    [InlineData("%")]
    [InlineData("_")]
    [InlineData("[a]")]
    public async Task A_search_reads_wildcards_as_plain_letters(string wildcard)
    {
        var tag = NewCode();
        var literal = await AddJobTypeAsync(sql.Owner, $"{tag} 100{wildcard} giá");
        var other = await AddJobTypeAsync(sql.Owner, $"{tag} 100xa giá");

        var found = await sql.Owner.GetFromJsonAsync<JsonElement>($"{JobTypes}?search={Uri.EscapeDataString($"{tag} 100{wildcard}")}");

        Assert.Equal(1, found.GetProperty("total").GetInt32());
        Assert.Equal(literal.IdOf(), found.GetProperty("items")[0].IdOf());
        Assert.NotEqual(other.IdOf(), literal.IdOf());
    }
}

/// <summary>QA of OMTS-227, GBR-01 and LABOR-11.2 on the job types of a profile: what has it keeps it, a new link cannot choose it.</summary>
public class LaborSettingsQaJobTypeLinkTests(AuthApiFactory factory) : IClassFixture<AuthApiFactory>
{
    private async Task<(HttpClient Owner, OMTS.Domain.Entities.Farming.FarmingZone Zone, JsonElement JobType)> SetupAsync()
    {
        var owner = await OwnerAsync(factory);
        return (owner, await factory.AddZoneAsync(NewCode()), await AddJobTypeAsync(owner));
    }

    private static async Task<JsonElement> DiscontinueAsync(HttpClient owner, JsonElement jobType) =>
        (await (await owner.PostAsJsonAsync($"{JobTypes}/{jobType.IdOf()}/discontinue", new { rowVersion = jobType.VersionOf() })).BodyAsync(HttpStatusCode.OK)).GetProperty("jobType").Clone();

    [Fact]
    public async Task A_profile_keeps_a_type_discontinued_later_through_other_edits()
    {
        var (owner, zone, jobType) = await SetupAsync();
        var (name, phone) = ($"Giữ {NewCode()}", NewPhone());
        var saved = await EmployeeRequests.CreateAsync(owner, EmployeeRequests.Profile(name, phone, zone.Id, b => b["jobTypeIds"] = new[] { jobType.IdOf() }));
        await DiscontinueAsync(owner, jobType);

        var edited = await owner.PutAsJsonAsync($"/api/employees/{EmployeeRequests.IdOf(saved)}", EmployeeRequests.Profile($"{name} sửa", phone, zone.Id, b =>
        {
            b["jobTypeIds"] = new[] { jobType.IdOf() };
            b["rowVersion"] = EmployeeRequests.RowVersionOf(saved);
        }));

        var view = (await edited.BodyAsync(HttpStatusCode.OK)).GetProperty("employee");
        Assert.Equal(jobType.IdOf(), view.GetProperty("jobTypes")[0].IdOf());
    }

    [Fact]
    public async Task A_discontinued_type_is_refused_on_a_new_link_on_an_add_and_on_an_edit_and_a_resume_offers_it_again()
    {
        var (owner, zone, jobType) = await SetupAsync();
        var (name, phone) = ($"Mới {NewCode()}", NewPhone());
        var saved = await EmployeeRequests.CreateAsync(owner, EmployeeRequests.Profile(name, phone, zone.Id));
        var stopped = await DiscontinueAsync(owner, jobType);

        var added = await owner.PostAsJsonAsync("/api/employees", EmployeeRequests.Profile($"Khác {NewCode()}", NewPhone(), zone.Id, b => b["jobTypeIds"] = new[] { jobType.IdOf() }));
        Dictionary<string, object?> Edit(string version) => EmployeeRequests.Profile(name, phone, zone.Id, b =>
        {
            b["jobTypeIds"] = new[] { jobType.IdOf() };
            b["rowVersion"] = version;
        });
        var edited = await owner.PutAsJsonAsync($"/api/employees/{EmployeeRequests.IdOf(saved)}", Edit(EmployeeRequests.RowVersionOf(saved)));

        Assert.Equal(("MSG-GEN-01", "Job types"), (await added.ErrorCodeAsync(), await EmployeeRequests.FieldAsync(added)));
        Assert.Equal(("MSG-GEN-01", "Job types"), (await edited.ErrorCodeAsync(), await EmployeeRequests.FieldAsync(edited)));

        await (await owner.PostAsJsonAsync($"{JobTypes}/{jobType.IdOf()}/resume", new { rowVersion = stopped.VersionOf() })).BodyAsync(HttpStatusCode.OK);

        var again = await owner.PutAsJsonAsync($"/api/employees/{EmployeeRequests.IdOf(saved)}", Edit(EmployeeRequests.RowVersionOf(saved)));
        Assert.Equal(jobType.IdOf(), (await again.BodyAsync(HttpStatusCode.OK)).GetProperty("employee").GetProperty("jobTypes")[0].IdOf());
    }

    [Fact]
    public async Task A_type_removed_after_it_was_discontinued_cannot_come_back()
    {
        var (owner, zone, jobType) = await SetupAsync();
        var (name, phone) = ($"Bỏ {NewCode()}", NewPhone());
        var saved = await EmployeeRequests.CreateAsync(owner, EmployeeRequests.Profile(name, phone, zone.Id, b => b["jobTypeIds"] = new[] { jobType.IdOf() }));
        await DiscontinueAsync(owner, jobType);
        var removed = await (await owner.PutAsJsonAsync($"/api/employees/{EmployeeRequests.IdOf(saved)}", EmployeeRequests.Profile(name, phone, zone.Id, b =>
        {
            b["jobTypeIds"] = Array.Empty<Guid>();
            b["rowVersion"] = EmployeeRequests.RowVersionOf(saved);
        }))).BodyAsync(HttpStatusCode.OK);

        var back = await owner.PutAsJsonAsync($"/api/employees/{EmployeeRequests.IdOf(saved)}", EmployeeRequests.Profile(name, phone, zone.Id, b =>
        {
            b["jobTypeIds"] = new[] { jobType.IdOf() };
            b["rowVersion"] = EmployeeRequests.RowVersionOf(removed);
        }));

        Assert.Equal("Job types", await EmployeeRequests.FieldAsync(back));
    }

    [Fact]
    public async Task Unknown_or_repeated_job_types_are_msg_gen_01_never_500()
    {
        var (owner, zone, jobType) = await SetupAsync();

        var unknown = await owner.PostAsJsonAsync("/api/employees", EmployeeRequests.Profile($"Lạ {NewCode()}", NewPhone(), zone.Id, b => b["jobTypeIds"] = new[] { Guid.NewGuid() }));
        var repeated = await owner.PostAsJsonAsync("/api/employees", EmployeeRequests.Profile($"Lặp {NewCode()}", NewPhone(), zone.Id, b => b["jobTypeIds"] = new[] { jobType.IdOf(), jobType.IdOf() }));

        Assert.Equal(("MSG-GEN-01", "Job types"), (await unknown.ErrorCodeAsync(), await EmployeeRequests.FieldAsync(unknown)));
        Assert.Equal(("MSG-GEN-01", "Job types"), (await repeated.ErrorCodeAsync(), await EmployeeRequests.FieldAsync(repeated)));
    }

    [Fact]
    public async Task A_zone_manager_chooses_a_type_from_the_options_for_a_new_profile()
    {
        var (owner, zone, jobType) = await SetupAsync();
        var (manager, _) = await EmployeesV2QaKit.StaffAsync(factory, (OMTS.Domain.Enums.StaffRole.ZoneManager, zone));
        Assert.Contains((await manager.GetFromJsonAsync<JsonElement>($"{JobTypes}/options")).EnumerateArray(), o => o.IdOf() == jobType.IdOf());

        var saved = await EmployeeRequests.CreateAsync(manager, EmployeeRequests.Profile($"Của quản lý {NewCode()}", NewPhone(), zone.Id, b =>
        {
            b["jobTypeIds"] = new[] { jobType.IdOf() };
            b["ownerApproved"] = true;
        }));

        Assert.Equal(jobType.Str("name"), saved.GetProperty("employee").GetProperty("jobTypes")[0].Str("name"));
        Assert.Equal(StatusIds.Usage.InUse, await factory.QueryAsync(db => db.JobTypes.Where(j => j.Id == jobType.IdOf()).Select(j => j.StatusId).SingleAsync()));
    }
}
