namespace OMTS.Application.Features.LaborCost.Calculation;

/// <summary>An advance that still counts (not reversed): when it was paid and how much.</summary>
public sealed record AdvanceEntry(Guid Id, DateOnly PaymentDate, DateTimeOffset RecordedAt, decimal Amount);

/// <summary>
/// Outstanding advances of one person (LAB-07/LAB-09), pure. Finalized payrolls deduct advances oldest first: the
/// total already deducted (<c>consumed</c>, the advance deduction lines of Approved or Fully Paid payslips) is
/// taken from the oldest advances, so a capped deduction can leave part of an advance outstanding.
/// </summary>
public static class AdvanceLedger
{
    /// <summary>What is still outstanding of each advance, oldest first.</summary>
    public static IReadOnlyList<(AdvanceEntry Advance, decimal Remaining)> Remaining(IEnumerable<AdvanceEntry> advances, decimal consumed)
    {
        var left = Math.Max(0, consumed);
        var result = new List<(AdvanceEntry, decimal)>();
        foreach (var advance in advances.OrderBy(a => a.PaymentDate).ThenBy(a => a.RecordedAt).ThenBy(a => a.Id))
        {
            var used = Math.Min(left, advance.Amount);
            left -= used;
            result.Add((advance, advance.Amount - used));
        }

        return result;
    }

    /// <summary>Outstanding total of the advances paid up to <paramref name="upTo"/> (the payroll month end).</summary>
    public static decimal OutstandingUpTo(IEnumerable<AdvanceEntry> advances, decimal consumed, DateOnly upTo) =>
        Remaining(advances, consumed).Where(r => r.Advance.PaymentDate <= upTo).Sum(r => r.Remaining);
}
