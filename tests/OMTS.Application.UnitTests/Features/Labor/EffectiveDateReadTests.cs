using OMTS.Domain.Entities.Labor;
using OMTS.Domain.Enums;

namespace OMTS.Application.UnitTests.Features.Labor;

/// <summary>BR-LABOR-12 and BR-LABOR-05: a day reads the rate or the settings in force on that day, whatever was entered later.</summary>
public sealed class EffectiveDateReadTests
{
    private static DateOnly D(int year, int month, int day) => new(year, month, day);

    private static readonly PayRate[] Rates =
    [
        new() { HourlyRate = 45_000, EffectiveFrom = D(2026, 11, 1) },
        new() { HourlyRate = 38_000, EffectiveFrom = D(2026, 1, 1) },
        new() { HourlyRate = 40_000, EffectiveFrom = D(2026, 6, 15) },
    ];

    [Theory]
    [InlineData(2026, 1, 1, 38_000)]
    [InlineData(2026, 6, 14, 38_000)]
    [InlineData(2026, 6, 15, 40_000)]
    [InlineData(2026, 10, 31, 40_000)]
    [InlineData(2026, 11, 1, 45_000)]
    [InlineData(2030, 1, 1, 45_000)]
    public void A_day_is_paid_at_the_rate_effective_on_it(int y, int m, int d, int expected)
    {
        Assert.Equal(expected, PayRate.EffectiveOn(Rates, D(y, m, d))!.HourlyRate);
    }

    [Fact]
    public void A_day_before_every_rate_has_no_rate() => Assert.Null(PayRate.EffectiveOn(Rates, D(2025, 12, 31)));

    [Fact]
    public void A_job_type_without_rates_has_no_rate() => Assert.Null(PayRate.EffectiveOn([], D(2026, 1, 1)));

    [Fact]
    public void A_new_rate_leaves_the_days_before_its_date_as_they_were()
    {
        var before = PayRate.EffectiveOn(Rates, D(2026, 12, 31))!.HourlyRate;
        PayRate[] withNew = [.. Rates, new() { HourlyRate = 50_000, EffectiveFrom = D(2027, 1, 1) }];

        Assert.Equal(before, PayRate.EffectiveOn(withNew, D(2026, 12, 31))!.HourlyRate);
        Assert.Equal(50_000, PayRate.EffectiveOn(withNew, D(2027, 1, 1))!.HourlyRate);
    }

    [Fact]
    public void A_day_uses_the_attendance_settings_in_force_on_it_and_none_before_the_first()
    {
        AttendanceSettingsVersion[] versions =
        [
            new() { EffectiveFrom = D(2026, 9, 1), RoundingUnit = AttendanceRoundingUnit.Minutes15 },
            new() { EffectiveFrom = D(2026, 3, 1), RoundingUnit = AttendanceRoundingUnit.Hour1 },
        ];

        Assert.Null(AttendanceSettingsVersion.EffectiveOn(versions, D(2026, 2, 28)));
        Assert.Equal(AttendanceRoundingUnit.Hour1, AttendanceSettingsVersion.EffectiveOn(versions, D(2026, 3, 1))!.RoundingUnit);
        Assert.Equal(AttendanceRoundingUnit.Hour1, AttendanceSettingsVersion.EffectiveOn(versions, D(2026, 8, 31))!.RoundingUnit);
        Assert.Equal(AttendanceRoundingUnit.Minutes15, AttendanceSettingsVersion.EffectiveOn(versions, D(2026, 9, 1))!.RoundingUnit);
    }

    [Theory]
    [InlineData(AttendanceRoundingUnit.None, 0)]
    [InlineData(AttendanceRoundingUnit.Minutes15, 15)]
    [InlineData(AttendanceRoundingUnit.Minutes30, 30)]
    [InlineData(AttendanceRoundingUnit.Hour1, 60)]
    public void A_rounding_unit_is_its_number_of_minutes(AttendanceRoundingUnit unit, int minutes) => Assert.Equal(minutes, (int)unit);
}
