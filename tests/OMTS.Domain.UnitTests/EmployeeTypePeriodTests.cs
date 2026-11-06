using OMTS.Domain.Entities.Labor;
using OMTS.Domain.Enums;

namespace OMTS.Domain.UnitTests;

/// <summary>BR-LABOR-01: the type of a day is the latest one from that day or before; a day before every period has the first type.</summary>
public class EmployeeTypePeriodTests
{
    private static readonly EmployeeTypePeriod[] Periods =
    [
        new() { Type = EmployeeType.Staff, EffectiveFrom = new DateOnly(2026, 6, 1) },
        new() { Type = EmployeeType.SeasonalWorker, EffectiveFrom = new DateOnly(2026, 1, 1) },
    ];

    [Fact]
    public void The_type_of_a_day_comes_from_the_latest_period_that_has_started()
    {
        Assert.Equal(EmployeeType.SeasonalWorker, EmployeeTypePeriod.TypeOn(Periods, new DateOnly(2026, 5, 31)));
        Assert.Equal(EmployeeType.Staff, EmployeeTypePeriod.TypeOn(Periods, new DateOnly(2026, 6, 1)));
        Assert.Equal(EmployeeType.Staff, EmployeeTypePeriod.TypeOn(Periods, new DateOnly(2027, 1, 1)));
    }

    [Fact]
    public void A_day_before_every_period_has_the_first_type_and_no_periods_have_no_type()
    {
        Assert.Equal(EmployeeType.SeasonalWorker, EmployeeTypePeriod.TypeOn(Periods, new DateOnly(2025, 12, 31)));
        Assert.Null(EmployeeTypePeriod.TypeOn([], new DateOnly(2026, 1, 1)));
    }
}
