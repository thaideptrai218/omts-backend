using OMTS.Domain.Entities.Harvest;

namespace OMTS.Application.Features.Harvest;

/// <summary>
/// The dates of the daily close check (GFR-05, BR-HARVEST-04, BR-HARVEST-06). A lot for sale counts its days from its harvest date, or from the day it was last reopened. With 3 days a lot
/// harvested on 26/10 can be sold until the end of 29/10 and is closed by the check at the start of 30/10; the check of 29/10 warns that it closes at the start of 30/10 (MSG-HARVEST-05).
/// </summary>
public static class HarvestLotAutoCloseRule
{
    /// <summary>The day the days before automatic close count from.</summary>
    public static DateOnly ForSaleSince(HarvestLot lot) => lot.ReopenedOn ?? lot.HarvestDate;

    /// <summary>The first check that closes a lot for sale since <paramref name="since"/>: harvest date plus the days is before that day.</summary>
    public static DateOnly ClosesOn(DateOnly since, int days) => since.AddDays(days + 1);

    /// <summary>The check of <paramref name="today"/> closes the lot (also a lot a missed check should have closed).</summary>
    public static bool ClosesAt(DateOnly since, int days, DateOnly today) => ClosesOn(since, days) <= today;

    /// <summary>The check of <paramref name="today"/> warns that the next check closes the lot.</summary>
    public static bool WarnsAt(DateOnly since, int days, DateOnly today) => ClosesOn(since, days) == today.AddDays(1);

    /// <summary>The latest day counted from that the check of <paramref name="checkDay"/> closes or warns: the lots it has to look at.</summary>
    public static DateOnly LatestSinceCheckedOn(DateOnly checkDay, int days) => checkDay.AddDays(-days);

    /// <summary>The latest day counted from that the check of <paramref name="checkDay"/> closes (HARVEST-03 step 4 counts them for the next check).</summary>
    public static DateOnly LatestSinceClosedOn(DateOnly checkDay, int days) => checkDay.AddDays(-days - 1);
}
