using OMTS.Application.Features.LaborCost.Calculation;
using OMTS.Domain.Enums;

namespace OMTS.Application.UnitTests.Features.LaborCost;

/// <summary>Net pay and the advance cap (LAB-07), and which advances are still outstanding (LAB-09).</summary>
public class PayslipBalanceAndAdvanceTests
{
    [Fact]
    public void Advance_deduction_is_capped_so_net_pay_is_never_negative()
    {
        var lines = new[] { (PayLineType.DailyWage, 1_000_000m), (PayLineType.Deduction, 200_000m) };

        var capped = PayslipBalance.Settle(lines, outstandingAdvances: 1_500_000);
        var whole = PayslipBalance.Settle(lines, outstandingAdvances: 300_000);

        Assert.Equal(new PayslipTotals(1_000_000, 200_000, 800_000, 0), capped);
        Assert.Equal(new PayslipTotals(1_000_000, 200_000, 300_000, 500_000), whole);
    }

    [Fact]
    public void Deductions_above_the_earnings_give_net_zero_and_no_advance_deduction()
    {
        var totals = PayslipBalance.Settle([(PayLineType.DailyWage, 300_000m), (PayLineType.Deduction, 500_000m)], 100_000);

        Assert.Equal((0m, 0m), (totals.AdvanceDeduction, totals.Net));
    }

    [Fact]
    public void Additions_are_earnings_and_stored_totals_include_the_advance_line()
    {
        var totals = PayslipBalance.Of([(PayLineType.MonthlySalary, 5_000_000m), (PayLineType.Addition, 200_000m), (PayLineType.AdvanceDeduction, 1_000_000m)]);

        Assert.Equal(new PayslipTotals(5_200_000, 0, 1_000_000, 4_200_000), totals);
    }

    [Fact]
    public void Deducted_amounts_consume_the_oldest_advances_first_and_can_leave_part_of_one()
    {
        var first = new AdvanceEntry(Guid.NewGuid(), new DateOnly(2026, 10, 5), DateTimeOffset.UnixEpoch, 600_000);
        var second = new AdvanceEntry(Guid.NewGuid(), new DateOnly(2026, 10, 12), DateTimeOffset.UnixEpoch, 600_000);

        var remaining = AdvanceLedger.Remaining([second, first], consumed: 800_000).ToDictionary(r => r.Advance.Id, r => r.Remaining);

        Assert.Equal(0m, remaining[first.Id]);
        Assert.Equal(400_000m, remaining[second.Id]);
    }

    [Fact]
    public void Only_advances_paid_up_to_the_month_end_are_outstanding_for_that_payroll()
    {
        var october = new AdvanceEntry(Guid.NewGuid(), new DateOnly(2026, 10, 20), DateTimeOffset.UnixEpoch, 500_000);
        var november = new AdvanceEntry(Guid.NewGuid(), new DateOnly(2026, 11, 2), DateTimeOffset.UnixEpoch, 700_000);

        Assert.Equal(500_000m, AdvanceLedger.OutstandingUpTo([october, november], 0, new DateOnly(2026, 10, 31)));
        Assert.Equal(1_200_000m, AdvanceLedger.OutstandingUpTo([october, november], 0, new DateOnly(2026, 11, 30)));
        Assert.Equal(200_000m, AdvanceLedger.OutstandingUpTo([october, november], 1_000_000, new DateOnly(2026, 11, 30)));
    }
}
