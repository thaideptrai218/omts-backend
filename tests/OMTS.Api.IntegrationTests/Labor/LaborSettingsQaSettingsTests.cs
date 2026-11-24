using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using OMTS.Api.IntegrationTests.Cooperatives;
using OMTS.Api.IntegrationTests.Employees;
using OMTS.Api.IntegrationTests.Infrastructure;
using OMTS.Domain.Constants;
using OMTS.Domain.Entities.Labor;
using static OMTS.Api.IntegrationTests.Infrastructure.AcsTestKit;
using static OMTS.Api.IntegrationTests.Infrastructure.FarmLabSqlServerKit;
using static OMTS.Api.IntegrationTests.Labor.LaborSettingsQaKit;
using static OMTS.Api.IntegrationTests.Labor.LaborSettingsTestKit;

namespace OMTS.Api.IntegrationTests.Labor;

/// <summary>QA of OMTS-227, LABOR-15 and LABOR-16: limits, the rounding unit and values that cannot be read, on one host as nothing here is saved twice on one date.</summary>
public class LaborSettingsQaInputTests(AuthApiFactory factory) : IClassFixture<AuthApiFactory>
{
    private static int _day;

    /// <summary>A date of its own for each save, as a date of a saved version would replace it. Read when the test runs.</summary>
    private static string NextDate() => new DateOnly(2010, 1, 1).AddDays(Interlocked.Increment(ref _day)).ToString("yyyy-MM-dd");

    private async Task<HttpResponseMessage> SaveAttendanceRawAsync(HttpClient owner, string fields)
    {
        var version = (await owner.GetFromJsonAsync<JsonElement>(AttendanceSettings)).VersionOf();
        return await owner.PostRawAsync(AttendanceSettings, $"{{{fields},\"rowVersion\":\"{version}\"}}");
    }

    [Theory]
    [InlineData("\"None\"", "None")]
    [InlineData("\"Minutes15\"", "Minutes15")]
    [InlineData("\"Minutes30\"", "Minutes30")]
    [InlineData("\"Hour1\"", "Hour1")]
    [InlineData("0", "None")]
    [InlineData("15", "Minutes15")]
    [InlineData("30", "Minutes30")]
    [InlineData("60", "Hour1")]
    [InlineData("\"minutes15\"", "Minutes15")]
    public async Task The_four_units_of_br_labor_05_are_read(string json, string stored)
    {
        var owner = await OwnerAsync(factory);
        var date = NextDate();

        var saved = await (await SaveAttendanceRawAsync(owner, $"\"roundingUnit\":{json},\"effectiveFrom\":\"{date}\"")).BodyAsync(HttpStatusCode.OK);

        Assert.Equal(stored, saved.GetProperty("settings").GetProperty("versions").EnumerateArray().Single(v => v.Str("effectiveFrom") == date).Str("roundingUnit"));
    }

    [Theory]
    [InlineData("7")]
    [InlineData("-1")]
    [InlineData("45")]
    [InlineData("\"7\"")]
    [InlineData("\"Minutes45\"")]
    [InlineData("\"Minutes15,Hour1\"")]
    [InlineData("15.5")]
    [InlineData("true")]
    [InlineData("null")]
    [InlineData("{}")]
    public async Task Any_other_unit_is_msg_gen_01_never_500(string json)
    {
        var owner = await OwnerAsync(factory);

        var refused = await SaveAttendanceRawAsync(owner, $"\"roundingUnit\":{json},\"effectiveFrom\":\"{NextDate()}\"");

        await refused.SrsProblemAsync(HttpStatusCode.BadRequest, "MSG-GEN-01", "MSG01");
    }

    [Theory]
    [InlineData("11:30", "12:30", "11:30:00", "12:30:00")]
    [InlineData(" 11:30 ", " 12:30 ", "11:30:00", "12:30:00")]
    [InlineData("00:00", "23:59", "00:00:00", "23:59:00")]
    [InlineData("11:30:00", "11:30:01", "11:30:00", "11:30:01")]
    public async Task A_break_in_order_is_saved(string start, string end, string shownStart, string shownEnd)
    {
        var owner = await OwnerAsync(factory);
        var date = NextDate();

        var saved = await (await SaveAttendanceRawAsync(owner, $"\"breakStart\":\"{start}\",\"breakEnd\":\"{end}\",\"roundingUnit\":\"None\",\"effectiveFrom\":\"{date}\"")).BodyAsync(HttpStatusCode.OK);

        var version = saved.GetProperty("settings").GetProperty("versions").EnumerateArray().Single(v => v.Str("effectiveFrom") == date);
        Assert.Equal((shownStart, shownEnd), (version.Str("breakStart"), version.Str("breakEnd")));
    }

    [Theory]
    [InlineData("23:00", "01:00", "Break end")] // no break over midnight: "the end is not after its start"
    [InlineData("12:00", "00:00", "Break end")]
    [InlineData("7:30", "8:30", "Break start")]
    [InlineData("24:00", "24:30", "Break start")]
    [InlineData("11:30 AM", "12:30 PM", "Break start")]
    [InlineData("11:30", "12:60", "Break end")]
    public async Task A_break_out_of_order_or_not_in_hh_mm_names_its_field(string start, string end, string field)
    {
        var owner = await OwnerAsync(factory);

        var refused = await SaveAttendanceRawAsync(owner, $"\"breakStart\":\"{start}\",\"breakEnd\":\"{end}\",\"roundingUnit\":\"None\",\"effectiveFrom\":\"{NextDate()}\"");

        var problem = await refused.SrsProblemAsync(HttpStatusCode.BadRequest, "MSG-GEN-01", "MSG01");
        Assert.Equal(field, problem.Str("field"));
    }

    [Theory]
    [InlineData("1130")]
    [InlineData("true")]
    [InlineData("[]")]
    public async Task A_time_that_is_no_text_is_msg_gen_01_never_500(string json)
    {
        var owner = await OwnerAsync(factory);

        var refused = await SaveAttendanceRawAsync(owner, $"\"breakStart\":{json},\"breakEnd\":\"12:30\",\"roundingUnit\":\"None\",\"effectiveFrom\":\"{NextDate()}\"");

        await refused.SrsProblemAsync(HttpStatusCode.BadRequest, "MSG-GEN-01", "MSG01");
    }

    [Fact]
    public async Task An_empty_body_is_msg_gen_01_on_every_write()
    {
        var owner = await OwnerAsync(factory);
        var jobType = await AddJobTypeAsync(owner);

        var responses = new[]
        {
            await owner.PostRawAsync(JobTypes, ""),
            await owner.PostRawAsync($"{JobTypes}/{jobType.IdOf()}/rates", ""),
            await owner.PutRawAsync($"{JobTypes}/{jobType.IdOf()}/name", ""),
            await owner.PostRawAsync($"{JobTypes}/{jobType.IdOf()}/discontinue", ""),
            await owner.PostRawAsync(AttendanceSettings, ""),
        };

        foreach (var response in responses)
        {
            await response.SrsProblemAsync(HttpStatusCode.BadRequest, "MSG-GEN-01", "MSG01");
        }
    }
}

/// <summary>QA of OMTS-227 on SQL Server: the row version of the cooperative that the settings share, and the history of the attendance settings.</summary>
public class LaborSettingsQaSharedVersionTests
{
    private static async Task<string> ProfileVersionAsync(HttpClient owner) => (await CooperativeProfileRequests.RowVersionAsync(owner))!;

    private static async Task<string> AttendanceVersionAsync(HttpClient owner) => (await owner.GetFromJsonAsync<JsonElement>(AttendanceSettings)).VersionOf();

    // The client list says so and the SRS keeps the settings on the Cooperative (LABOR-15 step 1): not a defect. A test keeps it known.
    [SqlServerFact]
    public async Task A_save_of_one_form_makes_the_other_stale_and_each_retries_with_the_version_it_is_given()
    {
        await using var database = SqlServerTestDatabase.Create();
        await using var api = await StartAsync(database);
        var (owner, _, _) = await OwnerWithCooperativeAsync(api);
        var stale = await AttendanceVersionAsync(owner);
        Assert.Equal(stale, await ProfileVersionAsync(owner));

        await (await owner.PostAsJsonAsync(AttendanceSettings, new { roundingUnit = "Minutes15", effectiveFrom = "2020-03-01", rowVersion = stale })).BodyAsync(HttpStatusCode.OK);

        var profile = await owner.PutAsJsonAsync("/api/cooperative/profile", CooperativeProfileRequests.Valid(stale));
        var profileProblem = await profile.SrsProblemAsync(HttpStatusCode.Conflict, "MSG06", null);

        var retried = await owner.PutAsJsonAsync("/api/cooperative/profile", CooperativeProfileRequests.Valid(profileProblem.Str("currentRowVersion")));
        Assert.Equal(HttpStatusCode.OK, retried.StatusCode);

        var staleAgain = await AttendanceVersionAsync(owner);
        await (await owner.PutAsJsonAsync("/api/cooperative/profile", CooperativeProfileRequests.Valid(staleAgain))).BodyAsync(HttpStatusCode.OK);
        var refused = await owner.PostAsJsonAsync(AttendanceSettings, new { roundingUnit = "Hour1", effectiveFrom = "2021-03-01", rowVersion = staleAgain });
        await refused.SrsProblemAsync(HttpStatusCode.Conflict, "MSG06", null);
        Assert.Equal(1, await QueryAsync(api, db => db.AttendanceSettingsVersions.CountAsync()));
    }

    [SqlServerFact]
    public async Task What_does_not_edit_the_cooperative_leaves_its_version_alone()
    {
        await using var database = SqlServerTestDatabase.Create();
        await using var api = await StartAsync(database);
        var (owner, _, cooperative) = await OwnerWithCooperativeAsync(api);
        var version = await AttendanceVersionAsync(owner);
        var zone = await AddZoneAsync(api, cooperative.Id);

        await AddJobTypeAsync(owner);
        await EmployeeRequests.CreateAsync(owner, EmployeeRequests.Profile($"Khác {NewCode()}", NewPhone(), zone.Id));

        Assert.Equal(version, await AttendanceVersionAsync(owner));
    }

    [SqlServerFact]
    public async Task A_replaced_version_keeps_its_row_the_others_stay_and_the_audit_holds_before_and_after()
    {
        await using var database = SqlServerTestDatabase.Create();
        await using var api = await StartAsync(database);
        var (owner, account, _) = await OwnerWithCooperativeAsync(api);
        async Task<JsonElement> SaveAsync(object body) => (await (await owner.PostAsJsonAsync(AttendanceSettings, body)).BodyAsync(HttpStatusCode.OK)).GetProperty("settings").Clone();
        var one = await SaveAsync(new { breakStart = "11:30", breakEnd = "12:30", roundingUnit = "Minutes15", effectiveFrom = "2020-03-01", rowVersion = await AttendanceVersionAsync(owner) });
        var two = await SaveAsync(new { breakStart = "11:00", breakEnd = "12:00", roundingUnit = "Minutes30", effectiveFrom = "2021-03-01", rowVersion = one.VersionOf() });
        var rows = await QueryAsync(api, db => db.AttendanceSettingsVersions.AsNoTracking().OrderBy(v => v.EffectiveFrom).ToListAsync());

        var replaced = await SaveAsync(new { roundingUnit = "Hour1", effectiveFrom = "2021-03-01", rowVersion = two.VersionOf() });

        var after = await QueryAsync(api, db => db.AttendanceSettingsVersions.AsNoTracking().OrderBy(v => v.EffectiveFrom).ToListAsync());
        Assert.Equal(2, after.Count);
        Assert.Equal((rows[0].Id, Convert.ToBase64String(rows[0].RowVersion), rows[0].UpdatedAt), (after[0].Id, Convert.ToBase64String(after[0].RowVersion), after[0].UpdatedAt));
        Assert.Equal(rows[1].Id, after[1].Id);
        Assert.Null(after[1].BreakStart);
        Assert.Equal(OMTS.Domain.Enums.AttendanceRoundingUnit.Hour1, after[1].RoundingUnit);
        Assert.Equal(JsonValueKind.Null, replaced.GetProperty("current").GetProperty("breakStart").ValueKind);

        var audit = await QueryAsync(api, db => db.AuditEvents.AsNoTracking().Where(e => e.Action == "AttendanceSettings.Changed").OrderBy(e => e.OccurredAt).ToListAsync());
        Assert.Equal(3, audit.Count);
        Assert.All(audit, e => Assert.Equal((account.Id, nameof(AttendanceSettingsVersion)), (e.ActorAccountId, e.RecordType)));
        Assert.Equal(rows[1].Id, audit[2].RecordId);
        Assert.Null(audit[0].Before);
        Assert.Contains("Minutes30", audit[2].Before);
        Assert.Contains("11:00", audit[2].Before);
        Assert.Contains("Hour1", audit[2].After);
    }
}
