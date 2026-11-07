using OMTS.Application.Features.Harvest;
using OMTS.Domain.Entities.Harvest;

namespace OMTS.Application.UnitTests.Features.Harvest;

/// <summary>GFR-05 dates: the example of the SRS, a Reopened lot counting from its reopen day, the warning the day before, a missed check, and the HARVEST-03 preview.</summary>
public class HarvestLotAutoCloseRuleTests
{
    private static readonly DateOnly Harvested = new(2026, 10, 26);

    // "With 3 days, a lot harvested on 26/10 can still be sold until the end of 29/10 and is closed at the check at the start of 30/10."
    [Theory]
    [InlineData(28, false, false)]
    [InlineData(29, false, true)]
    [InlineData(30, true, false)]
    // A missed check: the next one closes it, and never warns a lot it closes.
    [InlineData(31, true, false)]
    public void The_srs_example_is_warned_on_29_10_and_closed_on_30_10(int day, bool closes, bool warns)
    {
        var today = new DateOnly(2026, 10, day);

        Assert.Equal(new DateOnly(2026, 10, 30), HarvestLotAutoCloseRule.ClosesOn(Harvested, 3));
        Assert.Equal(closes, HarvestLotAutoCloseRule.ClosesAt(Harvested, 3, today));
        Assert.Equal(warns, HarvestLotAutoCloseRule.WarnsAt(Harvested, 3, today));
    }

    [Fact]
    public void A_reopened_lot_counts_from_its_reopen_day_and_an_open_lot_from_its_harvest_date()
    {
        var reopened = new HarvestLot { HarvestDate = Harvested, ReopenedOn = new DateOnly(2026, 11, 2) };

        Assert.Equal(Harvested, HarvestLotAutoCloseRule.ForSaleSince(new HarvestLot { HarvestDate = Harvested }));
        Assert.Equal(new DateOnly(2026, 11, 2), HarvestLotAutoCloseRule.ForSaleSince(reopened));
        Assert.Equal(new DateOnly(2026, 11, 6), HarvestLotAutoCloseRule.ClosesOn(HarvestLotAutoCloseRule.ForSaleSince(reopened), 3));
    }

    [Fact]
    public void One_day_closes_at_the_second_check_after_the_harvest()
    {
        Assert.False(HarvestLotAutoCloseRule.ClosesAt(Harvested, 1, Harvested.AddDays(1)));
        Assert.True(HarvestLotAutoCloseRule.WarnsAt(Harvested, 1, Harvested.AddDays(1)));
        Assert.True(HarvestLotAutoCloseRule.ClosesAt(Harvested, 1, Harvested.AddDays(2)));
    }

    // The check looks at every lot it closes or warns; HARVEST-03 counts the ones the next check closes.
    [Fact]
    public void The_check_looks_at_lots_counting_from_today_minus_the_days_and_the_preview_one_day_earlier()
    {
        var today = new DateOnly(2026, 10, 29);

        Assert.Equal(Harvested, HarvestLotAutoCloseRule.LatestSinceCheckedOn(today, 3));
        Assert.Equal(Harvested, HarvestLotAutoCloseRule.LatestSinceClosedOn(today.AddDays(1), 3));
        Assert.True(HarvestLotAutoCloseRule.WarnsAt(HarvestLotAutoCloseRule.LatestSinceCheckedOn(today, 3), 3, today));
        Assert.True(HarvestLotAutoCloseRule.ClosesAt(HarvestLotAutoCloseRule.LatestSinceClosedOn(today, 3), 3, today));
    }
}
