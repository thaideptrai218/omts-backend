using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using OMTS.Api.IntegrationTests.Employees;
using OMTS.Api.IntegrationTests.Infrastructure;
using OMTS.Domain.Constants;
using static OMTS.Api.IntegrationTests.Labor.LaborSettingsTestKit;

namespace OMTS.Api.IntegrationTests.Labor;

/// <summary>LABOR-16: the start day and automatic payroll, with the next two pay periods of BR-LABOR-13. Each test has its own cooperative.</summary>
public class PayrollSettingsTests
{
    private static async Task<JsonElement> ReadAsync(HttpClient owner) => await owner.GetFromJsonAsync<JsonElement>(PayrollSettings);

    private static IReadOnlyList<(string Start, string End, string Name)> PeriodsOf(JsonElement view) =>
        view.GetProperty("nextPeriods").EnumerateArray().Select(p => (p.Str("start")!, p.Str("end")!, p.Str("name")!)).ToList();

    [Fact]
    public async Task When_set_up_the_start_day_is_1_automatic_payroll_is_off_and_periods_are_whole_months()
    {
        await using var factory = new AuthApiFactory();
        var owner = await OwnerAsync(factory);

        var settings = await ReadAsync(owner);

        Assert.Equal(1, settings.GetProperty("payPeriodStartDay").GetInt32());
        Assert.False(settings.GetProperty("automaticPayroll").GetBoolean());
        // Whatever today is: the period of today and the one after, joined up, each from the first of a month.
        var (first, second) = (settings.GetProperty("nextPeriods")[0], settings.GetProperty("nextPeriods")[1]);
        var firstEnd = DateOnly.Parse(first.Str("end")!);
        Assert.Equal(1, DateOnly.Parse(first.Str("start")!).Day);
        Assert.Equal(firstEnd.AddDays(1), DateOnly.Parse(second.Str("start")!));
        Assert.Equal(1, firstEnd.AddDays(1).Day);
    }

    [Fact]
    public async Task The_preview_shows_the_srs_example_under_the_new_start_day_and_saves_nothing()
    {
        await using var factory = new AuthApiFactory();
        var owner = await OwnerAsync(factory);
        await factory.AddPayrollAsync("2026-10-01", "2026-10-31", StatusIds.Payroll.Finalized);

        var preview = await owner.GetFromJsonAsync<JsonElement>($"{PayrollSettings}/preview?startDay=26");

        Assert.Equal([("2026-11-01", "2026-11-25", "11/2026"), ("2026-11-26", "2026-12-25", "12/2026")], PeriodsOf(preview));
        Assert.Equal("MSG-LABOR-36", preview.Str("messageCode"));
        Assert.Equal(
            "With these settings, the next pay periods are 11/2026 (01/11/2026 to 25/11/2026) and 12/2026 (26/11/2026 to 25/12/2026). Do you want to save?",
            preview.Str("message"));
        Assert.Equal(1, (await ReadAsync(owner)).GetProperty("payPeriodStartDay").GetInt32());
    }

    [Fact]
    public async Task The_preview_names_automatic_payroll_when_it_is_chosen()
    {
        await using var factory = new AuthApiFactory();
        var owner = await OwnerAsync(factory);
        await factory.AddPayrollAsync("2026-10-01", "2026-10-31", StatusIds.Payroll.Draft);

        var preview = await owner.GetFromJsonAsync<JsonElement>($"{PayrollSettings}/preview?automaticPayroll=true");

        Assert.True(preview.GetProperty("automaticPayroll").GetBoolean());
        Assert.Equal(1, preview.GetProperty("payPeriodStartDay").GetInt32());
        Assert.Equal(
            "With these settings, the next pay periods are 11/2026 (01/11/2026 to 30/11/2026) and 12/2026 (01/12/2026 to 31/12/2026), and OMTS creates the payroll of each new pay period at its start. Do you want to save?",
            preview.Str("message"));
    }

    [Fact]
    public async Task A_save_follows_the_new_start_day_and_the_payrolls_keep_their_periods()
    {
        await using var factory = new AuthApiFactory();
        var owner = await OwnerAsync(factory);
        var payroll = await factory.AddPayrollAsync("2026-10-01", "2026-10-31", StatusIds.Payroll.Finalized);

        var response = await owner.PutAsJsonAsync(PayrollSettings, new { payPeriodStartDay = 26, automaticPayroll = true, rowVersion = (await ReadAsync(owner)).VersionOf() });

        var saved = await response.BodyAsync(HttpStatusCode.OK);
        Assert.Equal(("MSG-GEN-05", "Payroll settings saved."), (saved.Str("messageCode"), saved.Str("message")));
        var settings = saved.GetProperty("settings");
        Assert.Equal(26, settings.GetProperty("payPeriodStartDay").GetInt32());
        Assert.True(settings.GetProperty("automaticPayroll").GetBoolean());
        Assert.Equal([("2026-11-01", "2026-11-25", "11/2026"), ("2026-11-26", "2026-12-25", "12/2026")], PeriodsOf(settings));
        var stored = await factory.QueryAsync(db => db.Payrolls.SingleAsync(p => p.Id == payroll.Id));
        Assert.Equal((new DateOnly(2026, 10, 1), new DateOnly(2026, 10, 31)), (stored.PeriodStart, stored.PeriodEnd));
        Assert.Equal(1, await factory.AuditCountAsync("PayrollSettings.Changed"));
    }

    [Fact]
    public async Task The_same_settings_write_no_audit_row()
    {
        await using var factory = new AuthApiFactory();
        var owner = await OwnerAsync(factory);
        var body = new { payPeriodStartDay = 1, automaticPayroll = false, rowVersion = (await ReadAsync(owner)).VersionOf() };

        Assert.Equal(HttpStatusCode.OK, (await owner.PutAsJsonAsync(PayrollSettings, body)).StatusCode);

        Assert.Equal(0, await factory.AuditCountAsync("PayrollSettings.Changed"));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(29)]
    [InlineData(31)]
    [InlineData(-1)]
    public async Task A_start_day_outside_1_to_28_is_refused_on_save_and_on_preview(int day)
    {
        await using var factory = new AuthApiFactory();
        var owner = await OwnerAsync(factory);

        var saved = await owner.PutAsJsonAsync(PayrollSettings, new { payPeriodStartDay = day, automaticPayroll = false, rowVersion = (await ReadAsync(owner)).VersionOf() });
        var preview = await owner.GetAsync($"{PayrollSettings}/preview?startDay={day}");

        Assert.Equal(("MSG-GEN-01", "Pay period start day"), (await saved.ErrorCodeAsync(), await EmployeeRequests.FieldAsync(saved)));
        Assert.Equal(("MSG-GEN-01", "Pay period start day"), (await preview.ErrorCodeAsync(), await EmployeeRequests.FieldAsync(preview)));
        Assert.Equal(1, (await ReadAsync(owner)).GetProperty("payPeriodStartDay").GetInt32());
    }

    [Theory]
    [InlineData(1)]
    [InlineData(28)]
    public async Task The_first_and_the_last_start_day_are_saved(int day)
    {
        await using var factory = new AuthApiFactory();
        var owner = await OwnerAsync(factory);

        var saved = await owner.PutAsJsonAsync(PayrollSettings, new { payPeriodStartDay = day, automaticPayroll = false, rowVersion = (await ReadAsync(owner)).VersionOf() });

        Assert.Equal(day, (await saved.BodyAsync(HttpStatusCode.OK)).GetProperty("settings").GetProperty("payPeriodStartDay").GetInt32());
    }

    [Fact]
    public async Task Every_missing_field_is_listed()
    {
        await using var factory = new AuthApiFactory();
        var owner = await OwnerAsync(factory);

        var missing = await owner.PutAsJsonAsync(PayrollSettings, new { rowVersion = "" });
        var noVersion = await owner.PutAsJsonAsync(PayrollSettings, new { payPeriodStartDay = 1, automaticPayroll = false });
        var unreadable = await owner.GetAsync($"{PayrollSettings}/preview?startDay=abc");

        var problem = await missing.SrsProblemAsync(HttpStatusCode.BadRequest, "MSG-GEN-01", "MSG01");
        Assert.Equal(["Pay period start day", "Automatic payroll"], problem.GetProperty("fields").EnumerateArray().Select(f => f.GetString()));
        Assert.Equal("RowVersion", await EmployeeRequests.FieldAsync(noVersion));
        Assert.Equal(HttpStatusCode.BadRequest, unreadable.StatusCode);
    }
}
