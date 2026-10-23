using System.Net;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using OMTS.Api.IntegrationTests.Infrastructure;
using OMTS.Application.Features.LaborCost;
using OMTS.Domain.Constants;
using OMTS.Domain.Entities.Labor;
using OMTS.Domain.Enums;
using static OMTS.Api.IntegrationTests.Labor.LaborTestKit;

namespace OMTS.Api.IntegrationTests.Labor;

/// <summary>
/// LAB-06 vs LAB-08 (review): pay rates never start or end inside a finalized month (PERIOD_LOCKED), and an automatic
/// close never cuts into a finalized month that the closed version priced (MSG11).
/// </summary>
public class PayRateFinalizedMonthTests(AuthApiFactory factory) : IClassFixture<AuthApiFactory>
{
    private const string PayRates = "/api/pay-rates";

    private static int _slots;

    /// <summary>A month four months apart from every other one used by this class, so neighbours never share a finalized payroll.</summary>
    private static PayrollPeriod Spaced()
    {
        var start = new DateOnly(2004, 1, 1).AddMonths(4 * (Interlocked.Increment(ref _slots) - 1));
        return new PayrollPeriod(start.Year, start.Month);
    }

    /// <summary>An open-ended version from three months before <paramref name="month"/>, priced into a finalized payslip of the month.</summary>
    private async Task<PayRuleVersion> OpenEndedUsedByFinalizedAsync(LaborWorld w, Guid employeeId, PayrollPeriod month)
    {
        var rate = await w.AddRateAsync(month, employeeId, null, SalaryType.MonthlyFixed, 5_000_000, v => (v.ValidFrom, v.ValidTo) = (month.Start.AddMonths(-3), null));
        await factory.UseDbAsync(db =>
        {
            var payroll = new Payroll { Period = month.Key, PeriodStart = month.Start, PeriodEnd = month.End, PreparedByAccountId = w.OwnerAccount.Id, FinalizedAt = DateTimeOffset.UtcNow };
            var payslip = new LaborSettlement
            {
                PayrollId = payroll.Id, EmployeeId = employeeId, PeriodStart = month.Start, PeriodEnd = month.End, TotalObligation = 5_000_000,
                PreparedByAccountId = w.OwnerAccount.Id, StatusId = StatusIds.LaborSettlement.Approved,
            };
            db.AddRange(payroll, payslip, new PayLine
            {
                SettlementId = payslip.Id, EmployeeId = employeeId, PayRuleVersionId = rate.Id, PayDate = month.End, LineType = PayLineType.MonthlySalary, Amount = 5_000_000,
            });
        });
        return rate;
    }

    private Task<PayRuleVersion> ReloadAsync(Guid id) => factory.QueryAsync(db => db.PayRuleVersions.AsNoTracking().SingleAsync(v => v.Id == id));

    [Fact]
    public async Task A_new_rate_starting_inside_a_finalized_month_is_PERIOD_LOCKED_and_closes_nothing()
    {
        var w = await factory.WorldAsync();
        var employee = await w.AddEmployeeAsync();
        var month = Spaced();
        var used = await OpenEndedUsedByFinalizedAsync(w, employee.Id, month);

        var problem = await (await w.Owner.PostAsJsonAsync(PayRates, new { employeeId = employee.Id, salaryType = "MonthlyFixed", amount = 6_000_000, validFrom = month.Day(10) }))
            .ProblemAsync(HttpStatusCode.Conflict, "PERIOD_LOCKED");

        Assert.Equal(month.Key, problem.GetProperty("period").GetString());
        Assert.Null((await ReloadAsync(used.Id)).ValidTo);
        Assert.Equal(1, await factory.QueryAsync(db => db.PayRuleVersions.CountAsync(v => v.EmployeeId == employee.Id)));
    }

    [Fact]
    public async Task A_rate_ending_inside_a_finalized_month_is_PERIOD_LOCKED()
    {
        var w = await factory.WorldAsync();
        var employee = await w.AddEmployeeAsync();
        var worker = await w.AddWorkerAsync();
        var month = await w.FinalizeMonthAsync(Spaced());

        await (await w.Owner.PostAsJsonAsync(PayRates, new
        {
            workerId = worker.Id, salaryType = "DailyWage", amount = 300_000, validFrom = month.Start.AddMonths(-6), validTo = month.Day(15),
        })).ProblemAsync(HttpStatusCode.Conflict, "PERIOD_LOCKED");
        await (await w.Owner.PostAsJsonAsync($"{PayRates}/bulk", new
        {
            people = new[] { new { employeeId = (Guid?)employee.Id } }, rate = new { salaryType = "MonthlyFixed", amount = 5_000_000, validFrom = month.Day(2) },
        })).ProblemAsync(HttpStatusCode.Conflict, "PERIOD_LOCKED");

        Assert.Equal(0, await factory.QueryAsync(db => db.PayRuleVersions.CountAsync(v => v.WorkerId == worker.Id)));
    }

    [Fact]
    public async Task Closing_a_version_inside_a_month_it_priced_in_a_finalized_payslip_is_MSG11()
    {
        var w = await factory.WorldAsync();
        var employee = await w.AddEmployeeAsync();
        var month = Spaced();
        var used = await OpenEndedUsedByFinalizedAsync(w, employee.Id, month);

        // Bounded to the open month before (an open-ended one would itself cover the finalized month): the lock passes,
        // but closing the old version the day before cuts the finalized month it priced.
        var problem = await (await w.Owner.PostAsJsonAsync(PayRates, new
        {
            employeeId = employee.Id, salaryType = "MonthlyFixed", amount = 6_000_000, validFrom = month.Start.AddDays(-5), validTo = month.Start.AddDays(-1),
        }))
            .ProblemAsync(HttpStatusCode.Conflict, "MSG11");

        Assert.Contains($"finalized payslips {month.Label}", problem.GetProperty("detail").GetString());
        Assert.Null((await ReloadAsync(used.Id)).ValidTo);
        Assert.Equal(1, await factory.QueryAsync(db => db.PayRuleVersions.CountAsync(v => v.EmployeeId == employee.Id)));
    }

    [Fact]
    public async Task Closing_a_version_right_after_its_last_finalized_month_is_allowed()
    {
        var w = await factory.WorldAsync();
        var employee = await w.AddEmployeeAsync();
        var month = Spaced();
        var used = await OpenEndedUsedByFinalizedAsync(w, employee.Id, month);

        var created = await ExpectAsync(await w.Owner.PostAsJsonAsync(PayRates, new
        {
            employeeId = employee.Id, salaryType = "MonthlyFixed", amount = 6_000_000, validFrom = month.End.AddDays(1),
        }), HttpStatusCode.Created);

        Assert.Equal(month.End, (await ReloadAsync(used.Id)).ValidTo);
        Assert.Equal(used.Id, created.GetProperty("closedPrevious").GetProperty("id").GetGuid());
    }

    [Fact]
    public async Task Editing_the_dates_of_an_unused_version_into_a_finalized_month_is_PERIOD_LOCKED_but_unchanged_dates_are_fine()
    {
        var w = await factory.WorldAsync();
        var employee = await w.AddEmployeeAsync();
        var finalized = await w.FinalizeMonthAsync(Spaced());
        var openMonth = Spaced();
        var unused = await w.AddRateAsync(openMonth, employee.Id, null, SalaryType.MonthlyFixed, 5_000_000);
        object Edit(DateOnly from, DateOnly? to) => new
        {
            salaryType = "MonthlyFixed", amount = 5_500_000, validFrom = from, validTo = to, reason = "Điều chỉnh", rowVersion = Convert.ToBase64String(unused.RowVersion),
        };

        await (await w.Owner.PutAsJsonAsync($"{PayRates}/{unused.Id}", Edit(finalized.Day(3), null))).ProblemAsync(HttpStatusCode.Conflict, "PERIOD_LOCKED");
        await (await w.Owner.PutAsJsonAsync($"{PayRates}/{unused.Id}", Edit(finalized.Start.AddMonths(-2), finalized.Day(20)))).ProblemAsync(HttpStatusCode.Conflict, "PERIOD_LOCKED");
        var amountOnly = await ExpectAsync(await w.Owner.PutAsJsonAsync($"{PayRates}/{unused.Id}", Edit(openMonth.Start, openMonth.End)), HttpStatusCode.OK);

        Assert.Equal(5_500_000m, amountOnly.GetProperty("rate").GetProperty("amount").GetDecimal());
    }
}
