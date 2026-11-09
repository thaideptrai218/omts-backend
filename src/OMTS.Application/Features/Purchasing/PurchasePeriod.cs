using OMTS.Application.Common.Results;

namespace OMTS.Application.Features.Purchasing;

/// <summary>The period of the purchasing lists, reports and exports (PURCHASE-03, PURCHASE-04).</summary>
public static class PurchasePeriod
{
    /// <summary>Longest list, export and cost period, so one request never reads the whole history.</summary>
    public const int MaxYears = 5;

    /// <summary>
    /// The period defaults to the current month: a missing start is the first day of the month of the end, a missing end the
    /// last day of the current month. At most five years; a start after the end is MSG-GEN-01.
    /// </summary>
    public static Result<(DateOnly From, DateOnly To)> Resolve(DateOnly? requestedFrom, DateOnly? requestedTo, DateOnly today)
    {
        var from = requestedFrom ?? FirstOfMonth(requestedTo ?? today);
        var to = requestedTo ?? FirstOfMonth(today).AddMonths(1).AddDays(-1);
        return Check(from, to);
    }

    /// <summary>At most five years; a start after the end is MSG-GEN-01. Used by every list whose period has a default of its own (SALE-09: today).</summary>
    public static Result<(DateOnly From, DateOnly To)> Check(DateOnly from, DateOnly to)
    {
        if (from > to)
        {
            return AppError.Invalid("From date");
        }

        // Near the last year a DateOnly can hold there is no room for five more years, and so nothing to refuse.
        if (from.Year <= DateOnly.MaxValue.Year - MaxYears && to > from.AddYears(MaxYears))
        {
            return AppError.Invalid("To date", $"The period is at most {MaxYears} years.");
        }

        return (from, to);
    }

    private static DateOnly FirstOfMonth(DateOnly day) => new(day.Year, day.Month, 1);
}
