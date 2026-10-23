using OMTS.Application.Features.LaborCost;
using OMTS.Application.Features.LaborCost.Calculation;
using OMTS.Domain.Entities.Labor;
using OMTS.Domain.Enums;

namespace OMTS.Application.UnitTests.Features.LaborCost;

/// <summary>LAB-07 calculation: a worked example per salary type, allowances, deductions, piecework, missing rates, mid-month changes.</summary>
public class PayrollCalculatorTests
{
    private static readonly PayrollPeriod October = new(2026, 10);
    private static readonly PayeeKey Employee = new(PayeeKind.Employee, Guid.NewGuid());

    [Fact]
    public void MonthlyFixed_pays_the_full_amount_whatever_the_attendance()
    {
        var rate = Rate(SalaryType.MonthlyFixed, 8_000_000, new DateOnly(2026, 1, 1));
        var someDays = Days(1, 5, AttendanceResult.Present).Concat(Days(6, 10, AttendanceResult.UnpaidLeave)).ToList();

        var withAttendance = PayrollCalculator.Calculate(October, Input([rate], someDays));
        var withoutAttendance = PayrollCalculator.Calculate(October, Input([rate], []));

        var line = Assert.Single(withAttendance.Lines);
        Assert.Equal((PayLineType.MonthlySalary, 8_000_000m), (line.Type, line.Amount));
        Assert.Equal(8_000_000m, Assert.Single(withoutAttendance.Lines).Amount);
        Assert.Empty(withAttendance.MissingRateDates);
    }

    [Fact]
    public void MonthlyFixed_is_not_paid_to_someone_who_left_before_the_month()
    {
        var rate = Rate(SalaryType.MonthlyFixed, 8_000_000, new DateOnly(2026, 1, 1));

        var result = PayrollCalculator.Calculate(October, Input([rate], [], paysMonthly: false));

        Assert.Empty(result.Lines);
    }

    [Fact]
    public void PerWorkingDay_pays_amount_times_paid_days_over_standard_days_rounded_up()
    {
        // 18 Present + 2 PaidLeave = 20 paid days; UnpaidLeave is not paid. 8,000,000 × 20 ÷ 26 = 6,153,846.15… → 6,154,000.
        var rate = Rate(SalaryType.PerWorkingDay, 8_000_000, new DateOnly(2026, 1, 1), standardDays: 26);
        var days = Days(1, 18, AttendanceResult.Present).Concat(Days(19, 20, AttendanceResult.PaidLeave)).Concat(Days(21, 23, AttendanceResult.UnpaidLeave)).ToList();

        var line = Assert.Single(PayrollCalculator.Calculate(October, Input([rate], days)).Lines);

        Assert.Equal((PayLineType.MonthlySalary, 20m, 6_154_000m), (line.Type, line.Quantity!.Value, line.Amount));
    }

    [Fact]
    public void DailyWage_pays_one_line_per_paid_day()
    {
        var rate = Rate(SalaryType.DailyWage, 300_000, new DateOnly(2026, 1, 1));
        var days = Days(1, 5, AttendanceResult.Present).Concat(Days(6, 6, AttendanceResult.PaidLeave)).Concat(Days(7, 7, AttendanceResult.UnpaidLeave)).ToList();

        var lines = PayrollCalculator.Calculate(October, Input([rate], days)).Lines;

        Assert.Equal(6, lines.Count);
        Assert.All(lines, l => Assert.Equal((PayLineType.DailyWage, 300_000m), (l.Type, l.Amount)));
        Assert.Equal(1_800_000m, lines.Sum(l => l.Amount));
    }

    [Fact]
    public void Mid_month_daily_rate_change_uses_the_rate_valid_on_each_day()
    {
        var old = Rate(SalaryType.DailyWage, 300_000, new DateOnly(2026, 1, 1), validTo: new DateOnly(2026, 10, 15));
        var raised = Rate(SalaryType.DailyWage, 350_000, new DateOnly(2026, 10, 16));

        var lines = PayrollCalculator.Calculate(October, Input([raised, old], Days(14, 17, AttendanceResult.Present).ToList())).Lines;

        Assert.Equal([300_000m, 300_000m, 350_000m, 350_000m], lines.OrderBy(l => l.PayDate).Select(l => l.Amount));
        Assert.Equal([old.Id, old.Id, raised.Id, raised.Id], lines.OrderBy(l => l.PayDate).Select(l => l.PayRuleVersionId!.Value));
    }

    [Fact]
    public void Mid_month_change_of_a_per_working_day_salary_gives_one_line_per_version()
    {
        var old = Rate(SalaryType.PerWorkingDay, 7_800_000, new DateOnly(2026, 1, 1), validTo: new DateOnly(2026, 10, 15), standardDays: 26);
        var raised = Rate(SalaryType.PerWorkingDay, 9_100_000, new DateOnly(2026, 10, 16), standardDays: 26);

        var lines = PayrollCalculator.Calculate(October, Input([old, raised], Days(13, 18, AttendanceResult.Present).ToList())).Lines;

        // 3 days × 7,800,000 ÷ 26 = 900,000 and 3 days × 9,100,000 ÷ 26 = 1,050,000.
        Assert.Equal([900_000m, 1_050_000m], lines.Select(l => l.Amount));
    }

    [Fact]
    public void Allowances_per_month_and_per_paid_day_and_monthly_deductions()
    {
        var rate = Rate(SalaryType.DailyWage, 300_000, new DateOnly(2026, 1, 1), components:
        [
            Component(PayComponentKind.Allowance, "Phone", 100_000, PayComponentBasis.PerMonth),
            Component(PayComponentKind.Allowance, "Lunch", 30_000, PayComponentBasis.PerPaidDay),
            Component(PayComponentKind.Deduction, "Insurance", 200_000, PayComponentBasis.PerMonth),
        ]);
        var days = Days(1, 4, AttendanceResult.Present).Concat(Days(5, 5, AttendanceResult.UnpaidLeave)).ToList();

        var lines = PayrollCalculator.Calculate(October, Input([rate], days)).Lines;

        Assert.Equal(100_000m, lines.Single(l => l.Description == "Phone").Amount);
        Assert.Equal((4m, 120_000m), (lines.Single(l => l.Description.StartsWith("Lunch")).Quantity!.Value, lines.Single(l => l.Description.StartsWith("Lunch")).Amount));
        Assert.Equal((PayLineType.Deduction, 200_000m), (lines.Single(l => l.Description == "Insurance").Type, lines.Single(l => l.Description == "Insurance").Amount));
    }

    [Fact]
    public void Monthly_components_need_a_salary_line_in_the_month()
    {
        var rate = Rate(SalaryType.DailyWage, 300_000, new DateOnly(2026, 1, 1), components:
            [Component(PayComponentKind.Allowance, "Phone", 100_000, PayComponentBasis.PerMonth)]);

        Assert.Empty(PayrollCalculator.Calculate(October, Input([rate], [])).Lines);
    }

    [Fact]
    public void Piecework_is_quantity_times_rate_rounded_up_and_only_in_the_completion_month()
    {
        var pieces = new List<ConfirmedPiecework>
        {
            new(Guid.NewGuid(), new DateOnly(2026, 10, 22), 37, 2_500, "Clean ropes", "rope"),
            new(Guid.NewGuid(), new DateOnly(2026, 10, 23), 0, 2_500, "Nothing accepted", "rope"),
            new(Guid.NewGuid(), new DateOnly(2026, 11, 2), 10, 5_000, "Next month", "cage"),
        };
        var input = new PayeeMonth(new PayeeKey(PayeeKind.Team, Guid.NewGuid()), [], [], pieces, false);

        var line = Assert.Single(PayrollCalculator.Calculate(October, input).Lines);

        // 37 × 2,500 = 92,500 → 93,000.
        Assert.Equal((PayLineType.Piecework, 93_000m), (line.Type, line.Amount));
    }

    [Fact]
    public void Paid_days_without_a_valid_rate_are_missing_but_unpaid_leave_is_not()
    {
        var rate = Rate(SalaryType.DailyWage, 300_000, new DateOnly(2026, 10, 10));
        var days = Days(8, 9, AttendanceResult.Present).Concat(Days(7, 7, AttendanceResult.UnpaidLeave)).Concat(Days(10, 10, AttendanceResult.Present)).ToList();

        var result = PayrollCalculator.Calculate(October, Input([rate], days));

        Assert.Equal([new DateOnly(2026, 10, 8), new DateOnly(2026, 10, 9)], result.MissingRateDates);
        Assert.Equal(300_000m, Assert.Single(result.Lines).Amount);
        Assert.True(result.NeedsPayslip);
    }

    private static PayeeMonth Input(IReadOnlyList<PayRuleVersion> rates, IReadOnlyList<AttendanceDay> days, bool paysMonthly = true) =>
        new(Employee, rates, days, [], paysMonthly);

    private static IEnumerable<AttendanceDay> Days(int from, int to, AttendanceResult result) =>
        Enumerable.Range(from, to - from + 1).Select(d => new AttendanceDay(Guid.NewGuid(), new DateOnly(2026, 10, d), result));

    private static PayRuleVersion Rate(
        SalaryType type, decimal amount, DateOnly validFrom, DateOnly? validTo = null, int? standardDays = null, List<PayComponent>? components = null) =>
        new() { EmployeeId = Employee.Id, SalaryType = type, Amount = amount, ValidFrom = validFrom, ValidTo = validTo, StandardWorkingDays = standardDays, Components = components ?? [] };

    private static PayComponent Component(PayComponentKind kind, string name, decimal amount, PayComponentBasis basis) =>
        new() { Kind = kind, Name = name, Amount = amount, Basis = basis };
}
