using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using OMTS.Api.IntegrationTests.Employees;
using OMTS.Api.IntegrationTests.Infrastructure;
using static OMTS.Api.IntegrationTests.Infrastructure.AcsTestKit;
using static OMTS.Api.IntegrationTests.Labor.LaborSettingsTestKit;

namespace OMTS.Api.IntegrationTests.Labor;

/// <summary>LABOR-15: the break and the rounding unit from a date, with the earlier settings kept. Each test has its own history.</summary>
public class AttendanceSettingsTests
{
    private static async Task<JsonElement> ReadAsync(HttpClient owner) => await owner.GetFromJsonAsync<JsonElement>(LaborSettingsTestKit.AttendanceSettings);

    private static async Task<JsonElement> SaveAsync(HttpClient owner, object body) =>
        (await (await owner.PostAsJsonAsync(LaborSettingsTestKit.AttendanceSettings, body)).BodyAsync(HttpStatusCode.OK)).GetProperty("settings").Clone();

    [Fact]
    public async Task When_set_up_there_is_no_break_and_no_rounding()
    {
        await using var factory = new AuthApiFactory();
        var owner = await OwnerAsync(factory);

        var settings = await ReadAsync(owner);

        var current = settings.GetProperty("current");
        Assert.Equal(JsonValueKind.Null, current.GetProperty("breakStart").ValueKind);
        Assert.Equal(JsonValueKind.Null, current.GetProperty("breakEnd").ValueKind);
        Assert.Equal(JsonValueKind.Null, current.GetProperty("effectiveFrom").ValueKind);
        Assert.Equal("None", current.Str("roundingUnit"));
        Assert.Empty(settings.GetProperty("versions").EnumerateArray());
    }

    [Fact]
    public async Task New_settings_apply_from_their_date_and_the_earlier_ones_stay()
    {
        await using var factory = new AuthApiFactory();
        var owner = await OwnerAsync(factory);

        var response = await owner.PostAsJsonAsync(
            LaborSettingsTestKit.AttendanceSettings, new { breakStart = "11:30", breakEnd = "12:30", roundingUnit = "Minutes15", effectiveFrom = "2020-03-01", rowVersion = (await ReadAsync(owner)).VersionOf() });
        var saved = await response.BodyAsync(HttpStatusCode.OK);
        Assert.Equal(("MSG-GEN-05", "Attendance settings saved."), (saved.Str("messageCode"), saved.Str("message")));
        var first = saved.GetProperty("settings");
        Assert.Equal(("11:30:00", "12:30:00", "Minutes15"), (first.GetProperty("current").Str("breakStart"), first.GetProperty("current").Str("breakEnd"), first.GetProperty("current").Str("roundingUnit")));

        var second = await SaveAsync(owner, new { roundingUnit = "Hour1", effectiveFrom = "2021-03-01", rowVersion = first.VersionOf() });
        var third = await SaveAsync(owner, new { roundingUnit = "Minutes30", effectiveFrom = "2099-03-01", rowVersion = second.VersionOf() });

        // No break from 2021; 2099 is still to come, so it is not the current one.
        var current = third.GetProperty("current");
        Assert.Equal(("2021-03-01", "Hour1"), (current.Str("effectiveFrom"), current.Str("roundingUnit")));
        Assert.Equal(JsonValueKind.Null, current.GetProperty("breakStart").ValueKind);
        Assert.Equal(["2099-03-01", "2021-03-01", "2020-03-01"], third.GetProperty("versions").EnumerateArray().Select(v => v.Str("effectiveFrom")));
        Assert.Equal(3, await factory.AuditCountAsync("AttendanceSettings.Changed"));
    }

    [Fact]
    public async Task Settings_on_the_same_date_replace_that_version_and_the_audit_keeps_the_old_one()
    {
        await using var factory = new AuthApiFactory();
        var owner = await OwnerAsync(factory);
        var first = await SaveAsync(owner, new { breakStart = "11:30", breakEnd = "12:30", roundingUnit = "Minutes15", effectiveFrom = "2020-03-01", rowVersion = (await ReadAsync(owner)).VersionOf() });

        var replaced = await SaveAsync(owner, new { breakStart = "12:00", breakEnd = "13:00", roundingUnit = "Minutes30", effectiveFrom = "2020-03-01", rowVersion = first.VersionOf() });

        Assert.Single(replaced.GetProperty("versions").EnumerateArray());
        Assert.Equal("Minutes30", replaced.GetProperty("current").Str("roundingUnit"));
        Assert.Equal("12:00:00", replaced.GetProperty("current").Str("breakStart"));
        Assert.Equal(1, await factory.QueryAsync(db => db.AttendanceSettingsVersions.CountAsync()));
        Assert.Contains("Minutes15", await factory.LastAuditBeforeAsync("AttendanceSettings.Changed"));
    }

    [Fact]
    public async Task The_same_settings_on_the_same_date_write_no_audit_row()
    {
        await using var factory = new AuthApiFactory();
        var owner = await OwnerAsync(factory);
        var body = new { breakStart = "11:30", breakEnd = "12:30", roundingUnit = "Minutes15", effectiveFrom = "2020-03-01", rowVersion = (await ReadAsync(owner)).VersionOf() };

        await SaveAsync(owner, body);
        await SaveAsync(owner, body);

        Assert.Equal(1, await factory.AuditCountAsync("AttendanceSettings.Changed"));
    }

    [Theory]
    [InlineData("12:30", "11:30", "Break end")] // the end before the start
    [InlineData("11:30", "11:30", "Break end")] // the end at the start
    [InlineData("11:30", null, "Break end")]
    [InlineData(null, "12:30", "Break start")]
    public async Task A_break_needs_both_times_in_order(string? start, string? end, string field)
    {
        await using var factory = new AuthApiFactory();
        var owner = await OwnerAsync(factory);

        var refused = await owner.PostAsJsonAsync(
            LaborSettingsTestKit.AttendanceSettings, new { breakStart = start, breakEnd = end, roundingUnit = "None", effectiveFrom = "2020-03-01", rowVersion = (await ReadAsync(owner)).VersionOf() });

        Assert.Equal(("MSG-GEN-01", field), (await refused.ErrorCodeAsync(), await EmployeeRequests.FieldAsync(refused)));
        Assert.Equal(0, await factory.QueryAsync(db => db.AttendanceSettingsVersions.CountAsync()));
    }

    [Fact]
    public async Task The_date_and_the_rounding_unit_are_required_and_a_number_that_is_no_unit_is_refused()
    {
        await using var factory = new AuthApiFactory();
        var owner = await OwnerAsync(factory);
        var version = (await ReadAsync(owner)).VersionOf();

        var missing = await owner.PostAsJsonAsync(LaborSettingsTestKit.AttendanceSettings, new { rowVersion = version });
        var notAUnit = await owner.PostAsJsonAsync(LaborSettingsTestKit.AttendanceSettings, new { roundingUnit = 7, effectiveFrom = "2020-03-01", rowVersion = version });
        var unitByNumber = await owner.PostAsJsonAsync(LaborSettingsTestKit.AttendanceSettings, new { roundingUnit = 15, effectiveFrom = "2020-03-01", rowVersion = version });

        var problem = await missing.SrsProblemAsync(HttpStatusCode.BadRequest, "MSG-GEN-01", "MSG01");
        Assert.Equal(["Rounding unit", "Effective date"], problem.GetProperty("fields").EnumerateArray().Select(f => f.GetString()));
        Assert.Equal("Rounding unit", await EmployeeRequests.FieldAsync(notAUnit));
        Assert.Equal(HttpStatusCode.OK, unitByNumber.StatusCode);
    }

    [Theory]
    [InlineData("nonsense", "12:30", "Break start")]
    [InlineData("25:00", "26:00", "Break start")]
    [InlineData("11:30", "12h30", "Break end")]
    public async Task A_time_that_is_not_hh_mm_names_its_field(string start, string end, string field)
    {
        await using var factory = new AuthApiFactory();
        var owner = await OwnerAsync(factory);

        var refused = await owner.PostAsJsonAsync(LaborSettingsTestKit.AttendanceSettings, new { breakStart = start, breakEnd = end, roundingUnit = "None", effectiveFrom = "2020-03-01", rowVersion = "" });

        Assert.Equal(("MSG-GEN-01", field), (await refused.ErrorCodeAsync(), await EmployeeRequests.FieldAsync(refused)));
    }

    [Fact]
    public async Task The_time_the_view_shows_can_be_sent_back_and_blank_times_are_no_break()
    {
        await using var factory = new AuthApiFactory();
        var owner = await OwnerAsync(factory);

        var first = await SaveAsync(owner, new { breakStart = "11:30:00", breakEnd = "12:30:00", roundingUnit = "None", effectiveFrom = "2020-03-01", rowVersion = "" });
        var second = await SaveAsync(owner, new { breakStart = " ", breakEnd = "", roundingUnit = "None", effectiveFrom = "2021-03-01", rowVersion = first.VersionOf() });

        Assert.Equal("11:30:00", first.GetProperty("current").Str("breakStart"));
        Assert.Equal(JsonValueKind.Null, second.GetProperty("current").GetProperty("breakStart").ValueKind);
    }

    [Theory]
    [InlineData("{\"roundingUnit\":\"Fortnight\",\"effectiveFrom\":\"2020-03-01\",\"rowVersion\":\"\"}")]
    [InlineData("{\"roundingUnit\":\"None\",\"effectiveFrom\":\"01/03/2020\",\"rowVersion\":\"\"}")]
    public async Task An_unreadable_value_answers_msg_gen_01(string json)
    {
        await using var factory = new AuthApiFactory();
        var owner = await OwnerAsync(factory);

        var refused = await owner.PostAsync(LaborSettingsTestKit.AttendanceSettings, new StringContent(json, System.Text.Encoding.UTF8, "application/json"));

        Assert.Equal(HttpStatusCode.BadRequest, refused.StatusCode);
        Assert.Equal("MSG-GEN-01", await refused.ErrorCodeAsync());
    }

    [Fact]
    public async Task A_save_without_a_row_version_is_msg_gen_01()
    {
        await using var factory = new AuthApiFactory();
        var owner = await OwnerAsync(factory);

        var refused = await owner.PostAsJsonAsync(LaborSettingsTestKit.AttendanceSettings, new { roundingUnit = "None", effectiveFrom = "2020-03-01" });

        Assert.Equal("RowVersion", await EmployeeRequests.FieldAsync(refused));
    }
}
