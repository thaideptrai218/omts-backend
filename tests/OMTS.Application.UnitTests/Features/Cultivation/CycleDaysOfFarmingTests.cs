using OMTS.Application.Features.Cultivation.Cycles;

namespace OMTS.Application.UnitTests.Features.Cultivation;

/// <summary>PM 03/11 (OMTS-220): both the first and the last day count; the last day of an Open cycle is today.</summary>
public class CycleDaysOfFarmingTests
{
    private static readonly DateOnly Start = new(2026, 10, 1);
    private static readonly DateOnly Today = new(2026, 11, 4);

    [Fact]
    public void The_pm_example_start_on_the_first_and_last_lot_on_the_tenth_gives_ten_days() =>
        Assert.Equal(10, CycleDaysOfFarming.Count(Start, new DateOnly(2026, 10, 10), new DateOnly(2026, 10, 20), Today));

    [Fact]
    public void A_closed_cycle_without_harvest_counts_to_its_close_date() =>
        Assert.Equal(20, CycleDaysOfFarming.Count(Start, null, new DateOnly(2026, 10, 20), Today));

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void An_open_cycle_counts_to_today_with_or_without_a_lot(bool hasLot) =>
        Assert.Equal(35, CycleDaysOfFarming.Count(Start, hasLot ? new DateOnly(2026, 10, 10) : null, null, Today));

    [Fact]
    public void A_cycle_started_today_or_closed_on_its_first_day_has_one_day()
    {
        Assert.Equal(1, CycleDaysOfFarming.Count(Today, null, null, Today));
        Assert.Equal(1, CycleDaysOfFarming.Count(Start, null, Start, Today));
    }
}
