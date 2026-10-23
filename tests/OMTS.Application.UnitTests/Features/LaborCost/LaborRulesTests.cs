using OMTS.Application.Common.Results;
using OMTS.Application.Features.LaborCost;
using OMTS.Application.Features.LaborCost.Dtos;
using OMTS.Domain.Enums;

namespace OMTS.Application.UnitTests.Features.LaborCost;

/// <summary>Money rounding, payroll months, the period lock and the pay-rate timeline (LAB-05…LAB-08).</summary>
public class LaborRulesTests
{
    [Theory]
    [InlineData(92_500, 93_000)]
    [InlineData(93_000, 93_000)]
    [InlineData(1, 1_000)]
    [InlineData(0, 0)]
    public void Computed_amounts_are_rounded_up_to_the_next_thousand(decimal amount, decimal expected) =>
        Assert.Equal(expected, VndAmounts.RoundUpToThousand(amount));

    [Fact]
    public void Typed_amounts_must_be_thousands_and_quantities_two_decimals()
    {
        Assert.True(VndAmounts.IsThousands(1_500_000));
        Assert.False(VndAmounts.IsThousands(1_500_500));
        Assert.True(VndAmounts.HasAtMostTwoDecimals(12.75m));
        Assert.False(VndAmounts.HasAtMostTwoDecimals(12.755m));
        Assert.Equal("1,500,000", VndAmounts.Format(1_500_000));
    }

    [Fact]
    public void Payroll_period_is_a_calendar_month()
    {
        Assert.True(PayrollPeriod.TryParse("2028-02", out var february));
        Assert.Equal((new DateOnly(2028, 2, 1), new DateOnly(2028, 2, 29)), (february.Start, february.End));
        Assert.Equal(("2028-02", "02/2028"), (february.Key, february.Label));
        Assert.False(PayrollPeriod.TryParse("2026-13", out _));
        Assert.False(PayrollPeriod.TryParse("10/2026", out _));
        Assert.False(PayrollPeriod.TryParse(null, out _));
    }

    [Fact]
    public void Period_lock_finds_the_first_finalized_month_among_the_dates()
    {
        var finalized = new HashSet<string> { "2026-09" };

        Assert.Null(PayrollPeriodLockRule.FirstLocked([new DateOnly(2026, 10, 1)], finalized));
        Assert.Equal(new PayrollPeriod(2026, 9), PayrollPeriodLockRule.FirstLocked([new DateOnly(2026, 10, 1), new DateOnly(2026, 9, 30)], finalized));
    }

    [Fact]
    public void A_new_version_closes_the_earlier_open_ended_version_on_the_day_before()
    {
        var current = new RateSpan(Guid.NewGuid(), new DateOnly(2026, 1, 1), null);

        var placement = PayRateTimeline.Place([current], new DateOnly(2026, 11, 1), null, null, closeOpenEnded: true);

        Assert.Equal(new RatePlacement(null, current.Id, new DateOnly(2026, 10, 31)), placement);
    }

    [Fact]
    public void Any_other_overlap_is_refused()
    {
        var closed = new RateSpan(Guid.NewGuid(), new DateOnly(2026, 1, 1), new DateOnly(2026, 6, 30));
        var later = new RateSpan(Guid.NewGuid(), new DateOnly(2026, 9, 1), null);

        Assert.Equal(closed.Id, PayRateTimeline.Place([closed], new DateOnly(2026, 6, 1), null, null, true).OverlapsId);
        // An open-ended version starting later is not closed: the new open-ended version would overlap it.
        Assert.Equal(later.Id, PayRateTimeline.Place([later], new DateOnly(2026, 8, 1), null, null, true).OverlapsId);
        Assert.Null(PayRateTimeline.Place([later], new DateOnly(2026, 7, 1), new DateOnly(2026, 8, 31), null, true).OverlapsId);
        // An edit never closes another version.
        Assert.Equal(later.Id, PayRateTimeline.Place([later], new DateOnly(2026, 10, 1), null, null, closeOpenEnded: false).OverlapsId);
        Assert.Null(PayRateTimeline.Place([later], new DateOnly(2026, 10, 1), null, later.Id, false).OverlapsId);
    }

    [Fact]
    public void Pay_rate_rules_give_MSG01_first_then_MSG41()
    {
        var notThousands = PayRateInput.From(new PayRateBody(SalaryType.DailyWage, 300_500, null, [], new DateOnly(2026, 11, 1), null), forWorker: true);
        var workerMonthly = PayRateInput.From(new PayRateBody(SalaryType.MonthlyFixed, 5_000_000, null, [], new DateOnly(2026, 11, 1), null), forWorker: true);
        var noStandardDays = PayRateInput.From(new PayRateBody(SalaryType.PerWorkingDay, 5_000_000, null, [], new DateOnly(2026, 11, 1), null), forWorker: false);
        var dailyDeduction = PayRateInput.From(new PayRateBody(SalaryType.MonthlyFixed, 5_000_000, null,
            [new PayComponentInput(PayComponentKind.Deduction, "Insurance", 100_000, PayComponentBasis.PerPaidDay)], new DateOnly(2026, 11, 1), null), false);

        Assert.Equal(("MSG41", ErrorKind.Validation), (notThousands.Error!.Message.Code, notThousands.Error.Kind));
        Assert.Equal("Salary type", workerMonthly.Error!.Details!["field"]);
        Assert.Equal("Standard working days", noStandardDays.Error!.Details!["field"]);
        Assert.Equal("Component basis", dailyDeduction.Error!.Details!["field"]);
    }
}
