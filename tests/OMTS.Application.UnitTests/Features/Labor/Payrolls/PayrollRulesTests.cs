using OMTS.Application.Features.Labor.Payrolls;
using OMTS.Domain.Constants;
using OMTS.Domain.Enums;

namespace OMTS.Application.UnitTests.Features.Labor.Payrolls;

/// <summary>Names, periods, statuses and codes of payroll sheets (PAYROLL_API_CONTRACT).</summary>
public sealed class PayrollRulesTests
{
    [Fact]
    public void A_monthly_sheet_is_named_after_its_month_and_a_custom_one_after_its_dates()
    {
        Assert.Equal("Bảng lương tháng 10/2026", PayrollRules.NameOf(PayrollPeriodType.Monthly, new DateOnly(2026, 10, 1), new DateOnly(2026, 10, 31)));
        Assert.Equal("Bảng lương 05/10/2026 - 20/10/2026", PayrollRules.NameOf(PayrollPeriodType.Custom, new DateOnly(2026, 10, 5), new DateOnly(2026, 10, 20)));
    }

    [Theory]
    [InlineData(PayrollPeriodType.Monthly, "2026-02-01", "2026-02-28", true)]
    [InlineData(PayrollPeriodType.Monthly, "2028-02-01", "2028-02-29", true)]
    [InlineData(PayrollPeriodType.Monthly, "2026-02-01", "2026-02-27", false)]
    [InlineData(PayrollPeriodType.Monthly, "2026-02-02", "2026-02-28", false)]
    [InlineData(PayrollPeriodType.Custom, "2026-02-02", "2026-02-03", true)]
    [InlineData(PayrollPeriodType.Custom, "2026-02-03", "2026-02-02", false)]
    [InlineData(PayrollPeriodType.Custom, "2026-01-01", "2026-12-31", true)]
    [InlineData(PayrollPeriodType.Custom, "2026-01-01", "2027-01-01", true)]
    [InlineData(PayrollPeriodType.Custom, "2026-01-01", "2027-01-02", false)]
    public void Periods_are_whole_months_or_at_most_a_year(PayrollPeriodType type, string from, string to, bool valid) =>
        Assert.Equal(valid, PayrollRules.IsValidPeriod(type, DateOnly.Parse(from), DateOnly.Parse(to)));

    [Theory]
    [InlineData(StatusIds.Payroll.Draft, PayrollSheetStatus.Provisional)]
    [InlineData(StatusIds.Payroll.AwaitingFinalization, PayrollSheetStatus.Provisional)]
    [InlineData(StatusIds.Payroll.SentBack, PayrollSheetStatus.Provisional)]
    [InlineData(StatusIds.Payroll.Creating, PayrollSheetStatus.Creating)]
    [InlineData(StatusIds.Payroll.Finalized, PayrollSheetStatus.Finalized)]
    [InlineData(StatusIds.Payroll.Cancelled, PayrollSheetStatus.Cancelled)]
    public void Catalog_statuses_read_as_the_contract_statuses(int statusId, PayrollSheetStatus expected) =>
        Assert.Equal(expected, PayrollRules.StatusOf(statusId));

    [Fact]
    public void Provisional_covers_the_draft_and_the_states_of_srs_v8()
    {
        Assert.Equal([StatusIds.Payroll.Draft, StatusIds.Payroll.AwaitingFinalization, StatusIds.Payroll.SentBack, StatusIds.Payroll.Finalized],
            PayrollRules.StatusIdsOf([PayrollSheetStatus.Provisional, PayrollSheetStatus.Finalized]));
    }

    [Theory]
    [InlineData("2026-10", true)]
    [InlineData("2026-13", false)]
    [InlineData("2026-00", false)]
    [InlineData("2026-1", false)]
    [InlineData("10/2026", false)]
    [InlineData(null, false)]
    public void An_expense_month_is_yyyy_MM(string? text, bool valid) => Assert.Equal(valid, PayrollRules.IsExpenseMonth(text));

    [Fact]
    public void Codes_have_the_prefix_and_six_digits_and_file_names_stay_plain()
    {
        Assert.Equal("BL000007", PayrollRules.CodeOf("BL", 7));
        Assert.Equal("PL1234567", PayrollRules.CodeOf("PL", 1_234_567));
        Assert.Equal("payroll-BL-000-7_x.xlsx", PayrollRules.SafeFileName("payroll-BL/000?7_x.xlsx"));
    }
}
