using OMTS.Application.Features.Farming;
using OMTS.Application.Features.Labor;
using OMTS.Domain.Entities.Labor;
using OMTS.Domain.Enums;

namespace OMTS.Application.UnitTests.Features.Labor;

/// <summary>The salary setup of an employee: a month reads the setup in force on it, and the fields follow the salary type.</summary>
public sealed class EmployeeSalarySetupTests
{
    private static DateOnly D(int year, int month, int day) => new(year, month, day);

    private static readonly EmployeeSalarySetup[] Setups =
    [
        new() { Type = SalaryType.FixedMonthly, MonthlyAmount = 9_000_000, EffectiveFrom = D(2026, 12, 1) },
        new() { Type = SalaryType.Hourly, EffectiveFrom = D(2026, 1, 1) },
        new() { Type = SalaryType.FixedMonthly, MonthlyAmount = 8_000_000, EffectiveFrom = D(2026, 6, 1) },
    ];

    [Theory]
    [InlineData(2026, 1, 1, SalaryType.Hourly, null)]
    [InlineData(2026, 5, 31, SalaryType.Hourly, null)]
    [InlineData(2026, 6, 1, SalaryType.FixedMonthly, 8_000_000)]
    [InlineData(2026, 11, 30, SalaryType.FixedMonthly, 8_000_000)]
    [InlineData(2026, 12, 1, SalaryType.FixedMonthly, 9_000_000)]
    public void A_day_reads_the_setup_effective_on_it(int y, int m, int d, SalaryType type, int? amount)
    {
        var setup = EmployeeSalarySetup.EffectiveOn(Setups, D(y, m, d))!;
        Assert.Equal(type, setup.Type);
        Assert.Equal(amount, (int?)setup.MonthlyAmount);
    }

    [Fact]
    public void A_day_before_every_setup_has_none() => Assert.Null(EmployeeSalarySetup.EffectiveOn(Setups, D(2025, 12, 31)));

    [Fact]
    public void A_seasonal_worker_without_a_setup_is_paid_by_the_hour() =>
        Assert.Equal(SalaryType.Hourly, EmployeeSalarySetup.DefaultFor(EmployeeType.SeasonalWorker));

    [Fact]
    public void A_staff_employee_without_a_setup_has_no_salary_yet() => Assert.Null(EmployeeSalarySetup.DefaultFor(EmployeeType.Staff));

    [Fact]
    public void A_fixed_salary_keeps_its_whole_amount()
    {
        var check = new FieldCheck();
        var (type, amount) = EmployeeSalaryService.Fields(check, SalaryType.FixedMonthly, 7_500_000.0m);
        Assert.True(check.IsValid);
        Assert.Equal(SalaryType.FixedMonthly, type);
        Assert.Equal(7_500_000m, amount);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("0")]
    [InlineData("-1")]
    [InlineData("1000.5")]
    [InlineData("10000000000")]
    public void A_fixed_salary_needs_a_whole_amount_above_zero(string? amount)
    {
        var check = new FieldCheck();
        EmployeeSalaryService.Fields(check, SalaryType.FixedMonthly, amount is null ? null : decimal.Parse(amount, System.Globalization.CultureInfo.InvariantCulture));
        Assert.Equal(["Monthly amount"], check.Invalid);
    }

    [Fact]
    public void Hourly_pay_has_no_monthly_amount()
    {
        var check = new FieldCheck();
        EmployeeSalaryService.Fields(check, SalaryType.Hourly, 5_000_000m);
        Assert.Equal(["Monthly amount"], check.Invalid);

        var valid = new FieldCheck();
        Assert.Equal((SalaryType.Hourly, (decimal?)null), EmployeeSalaryService.Fields(valid, SalaryType.Hourly, null));
        Assert.True(valid.IsValid);
    }

    [Fact]
    public void A_missing_or_unknown_type_is_refused()
    {
        var missing = new FieldCheck();
        EmployeeSalaryService.Fields(missing, null, null);
        Assert.Equal(["Salary type"], missing.Invalid);

        var unknown = new FieldCheck();
        EmployeeSalaryService.Fields(unknown, (SalaryType)9, null);
        Assert.Equal(["Salary type"], unknown.Invalid);
    }
}
