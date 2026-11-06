using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using OMTS.Api.IntegrationTests.Infrastructure;
using static OMTS.Api.IntegrationTests.Infrastructure.AcsTestKit;
using static OMTS.Api.IntegrationTests.Infrastructure.FarmLabSqlServerKit;
using static OMTS.Api.IntegrationTests.Labor.LaborSettingsTestKit;

namespace OMTS.Api.IntegrationTests.Labor;

/// <summary>QA M1 of OMTS-227: what is stored and compared is the name as a person reads it, on the real unique index of SQL Server.</summary>
public class LaborSettingsFixNameTests(LaborSqlFixture sql) : IClassFixture<LaborSqlFixture>
{
    private const string ZeroWidthSpace = "​";
    private const string NoBreakSpace = " ";
    private const int MaxName = 100;

    private Task<string> StoredNameAsync(Guid id) =>
        QueryAsync(sql.Api, db => db.JobTypes.Where(j => j.Id == id).Select(j => j.Name).SingleAsync());

    [SqlServerFact]
    public async Task A_new_name_is_stored_and_answered_in_its_one_spelling()
    {
        var tag = NewCode();
        var clean = $"{tag} Thu hoạch hàu";
        var typed = $" {tag}{NoBreakSpace}Thu\t hoạch\n{ZeroWidthSpace}hàu{ZeroWidthSpace} ".Normalize(NormalizationForm.FormD);

        var jobType = await AddJobTypeAsync(sql.Owner, typed);

        Assert.Equal(clean, jobType.Str("name"));
        Assert.Equal(clean, await StoredNameAsync(jobType.IdOf()));
        Assert.True((await StoredNameAsync(jobType.IdOf())).IsNormalized(NormalizationForm.FormC));
    }

    [SqlServerFact]
    public async Task A_new_name_on_a_rename_is_stored_in_its_one_spelling_and_so_audited()
    {
        var jobType = await AddJobTypeAsync(sql.Owner);
        var clean = $"{NewCode()} Vệ sinh dây giàn";

        var renamed = await sql.Owner.PutAsJsonAsync($"{JobTypes}/{jobType.IdOf()}/name", new { name = clean.Replace(" ", "  ").Normalize(NormalizationForm.FormD), rowVersion = jobType.VersionOf() });

        Assert.Equal(clean, (await renamed.BodyAsync(HttpStatusCode.OK)).GetProperty("jobType").Str("name"));
        Assert.Equal(clean, await StoredNameAsync(jobType.IdOf()));
        var audit = await QueryAsync(sql.Api, db => db.AuditEvents.SingleAsync(e => e.RecordId == jobType.IdOf() && e.Action == "JobType.Renamed"));
        Assert.Equal(clean, audit.RecordCode);
    }

    // The same name in another form is no change: no version moves, and no audit row says it was renamed.
    [SqlServerFact]
    public async Task A_rename_to_another_form_of_its_own_name_changes_nothing()
    {
        var name = $"{NewCode()} Thu hoạch";
        var jobType = await AddJobTypeAsync(sql.Owner, name);

        var renamed = await sql.Owner.PutAsJsonAsync($"{JobTypes}/{jobType.IdOf()}/name", new { name = name.Normalize(NormalizationForm.FormD), rowVersion = jobType.VersionOf() });

        Assert.Equal(jobType.VersionOf(), (await renamed.BodyAsync(HttpStatusCode.OK)).GetProperty("jobType").VersionOf());
        Assert.Equal(0, await QueryAsync(sql.Api, db => db.AuditEvents.CountAsync(e => e.RecordId == jobType.IdOf() && e.Action == "JobType.Renamed")));
    }

    [SqlServerTheory]
    [InlineData(ZeroWidthSpace)]
    [InlineData("­⁠﻿")]
    [InlineData(" \t\n ")]
    public async Task A_name_of_only_invisible_characters_is_empty(string invisible)
    {
        var jobType = await AddJobTypeAsync(sql.Owner);
        var before = await StoredNameAsync(jobType.IdOf());

        var added = await sql.Owner.PostAsJsonAsync(JobTypes, new { name = invisible, hourlyRate = 1, effectiveFrom = "2020-01-01" });
        var renamed = await sql.Owner.PutAsJsonAsync($"{JobTypes}/{jobType.IdOf()}/name", new { name = invisible, rowVersion = jobType.VersionOf() });

        Assert.Equal(["Name"], (await added.SrsProblemAsync(HttpStatusCode.BadRequest, "MSG-GEN-01", "MSG01")).GetProperty("fields").EnumerateArray().Select(f => f.GetString()));
        Assert.Equal(["Name"], (await renamed.SrsProblemAsync(HttpStatusCode.BadRequest, "MSG-GEN-01", "MSG01")).GetProperty("fields").EnumerateArray().Select(f => f.GetString()));
        Assert.Equal(before, await StoredNameAsync(jobType.IdOf()));
    }

    [SqlServerFact]
    public async Task The_longest_name_counts_what_is_stored_not_what_was_typed()
    {
        var clean = $"{NewCode()} ";
        clean += new string('ạ', MaxName - clean.Length);

        var added = await sql.Owner.PostAsJsonAsync(JobTypes, new { name = clean.Normalize(NormalizationForm.FormD), hourlyRate = 1, effectiveFrom = "2020-01-01" });

        Assert.Equal(clean, (await added.BodyAsync(HttpStatusCode.Created)).GetProperty("jobType").Str("name"));
    }

    [SqlServerFact]
    public async Task A_search_in_another_form_finds_the_name()
    {
        var tag = NewCode();
        var jobType = await AddJobTypeAsync(sql.Owner, $"{tag} Thu hoạch");

        var typed = $"{tag}  Thu{NoBreakSpace}hoạch".Normalize(NormalizationForm.FormD);

        var found = await sql.Owner.GetFromJsonAsync<JsonElement>($"{JobTypes}?search={Uri.EscapeDataString(typed)}");

        Assert.Equal(jobType.IdOf(), found.GetProperty("items").EnumerateArray().Single().IdOf());
    }
}
