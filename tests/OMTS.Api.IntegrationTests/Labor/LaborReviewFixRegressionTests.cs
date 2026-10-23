using System.Net;
using System.Net.Http.Json;
using OMTS.Api.IntegrationTests.Infrastructure;
using OMTS.Application.Features.LaborCost;
using OMTS.Domain.Entities.Labor;
using OMTS.Domain.Enums;
using static OMTS.Api.IntegrationTests.Labor.LaborTestKit;

namespace OMTS.Api.IntegrationTests.Labor;

/// <summary>
/// Residual gaps of the PR #13 review fixes found by QA. Expected results follow the review wording and the
/// fixes already in place (PERIOD_LOCKED for pay rates, the assignment precedent for unchanged assignees).
/// </summary>
public class LaborReviewFixRegressionTests(AuthApiFactory factory) : IClassFixture<AuthApiFactory>
{
    private const string PayRates = "/api/pay-rates";

    private static int _slots;

    /// <summary>Months far from the others of this class (7 apart) so neighbours never share a finalized payroll.</summary>
    private static PayrollPeriod Distant()
    {
        var start = new DateOnly(2010, 1, 1).AddMonths(7 * (Interlocked.Increment(ref _slots) - 1));
        return new PayrollPeriod(start.Year, start.Month);
    }

    private static object Edit(PayRuleVersion version, DateOnly from, DateOnly? to) => new
    {
        salaryType = "MonthlyFixed", amount = 5_500_000, validFrom = from, validTo = to, reason = "Điều chỉnh", rowVersion = Convert.ToBase64String(version.RowVersion),
    };

    [Fact]
    public async Task Clearing_the_end_of_an_unused_version_so_that_it_covers_a_finalized_month_is_PERIOD_LOCKED()
    {
        var w = await factory.WorldAsync();
        var employee = await w.AddEmployeeAsync();
        var finalized = await w.FinalizeMonthAsync(Distant());
        var rate = await w.AddRateAsync(finalized, employee.Id, null, SalaryType.MonthlyFixed, 5_000_000,
            v => (v.ValidFrom, v.ValidTo) = (finalized.Start.AddMonths(-3), finalized.Start.AddDays(-1)));

        // Open-ended again: the version now prices the finalized month, whose rate history must not change.
        await (await w.Owner.PutAsJsonAsync($"{PayRates}/{rate.Id}", Edit(rate, rate.ValidFrom, null)))
            .ProblemAsync(HttpStatusCode.Conflict, "PERIOD_LOCKED");
    }

    [Fact]
    public async Task An_open_ended_new_rate_starting_before_a_finalized_month_is_PERIOD_LOCKED_because_it_covers_that_month()
    {
        var w = await factory.WorldAsync();
        var employee = await w.AddEmployeeAsync();
        var finalized = await w.FinalizeMonthAsync(Distant());

        await (await w.Owner.PostAsJsonAsync(PayRates, new
        {
            employeeId = employee.Id, salaryType = "MonthlyFixed", amount = 5_000_000, validFrom = finalized.Start.AddMonths(-2),
        })).ProblemAsync(HttpStatusCode.Conflict, "PERIOD_LOCKED");
    }

    [Fact]
    public async Task An_unchanged_attendance_row_of_an_employee_moved_out_of_the_zone_can_be_saved_again()
    {
        var w = await factory.WorldAsync();
        var employee = await w.AddEmployeeAsync();
        var date = NewMonth().Day(18);
        var sheet = $"{w.Attendance}/{date:yyyy-MM-dd}";
        var rows = new { rows = new[] { new { employeeId = employee.Id, result = "Present" } } };
        await ExpectAsync(await w.Owner.PutAsJsonAsync(sheet, rows), HttpStatusCode.OK);
        await factory.UseDbAsync(db => db.EmployeeWorkingZones.RemoveRange(db.EmployeeWorkingZones.Where(z => z.EmployeeId == employee.Id)));

        // Nothing in the saved record changes, so the zone rule should not block the re-save (an unchanged assignee of an edit is accepted).
        await ExpectAsync(await w.Owner.PutAsJsonAsync(sheet, rows), HttpStatusCode.OK);
    }
}
