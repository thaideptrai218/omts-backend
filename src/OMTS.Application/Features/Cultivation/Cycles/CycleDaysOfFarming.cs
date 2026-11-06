namespace OMTS.Application.Features.Cultivation.Cycles;

/// <summary>Days of farming of a cycle (BR-CYCLE-06), worked out when shown and never stored.</summary>
public static class CycleDaysOfFarming
{
    /// <summary>
    /// PM 03/11: both the first and the last day count (start 01/10 and last harvest lot 10/10 give 10 days). The last day of an
    /// Open cycle is today; of a Closed one, the date of its last harvest lot, or the close date when it has no harvest.
    /// </summary>
    /// <param name="closeDate">Null for an Open cycle.</param>
    public static int Count(DateOnly startDate, DateOnly? lastLotDate, DateOnly? closeDate, DateOnly today)
    {
        var lastDay = closeDate is { } closed ? lastLotDate ?? closed : today;
        return lastDay.DayNumber - startDate.DayNumber + 1;
    }
}
