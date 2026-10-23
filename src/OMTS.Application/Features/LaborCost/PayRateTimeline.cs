namespace OMTS.Application.Features.LaborCost;

/// <summary>Validity of one existing pay rate version of a person.</summary>
public sealed record RateSpan(Guid Id, DateOnly ValidFrom, DateOnly? ValidTo);

/// <summary>Where a new version fits: the version it overlaps (refused), or the open-ended version it closes.</summary>
public sealed record RatePlacement(Guid? OverlapsId, Guid? ClosesId, DateOnly? ClosesOn);

/// <summary>
/// LAB-06 timeline rule (pure): versions of a person never overlap. A new version whose person has an open-ended
/// version starting earlier closes that version on ValidFrom − 1 day; any other overlap is refused (MSG03).
/// </summary>
public static class PayRateTimeline
{
    /// <param name="exceptId">The version being edited, ignored in the check.</param>
    /// <param name="closeOpenEnded">True for a new version (create, bulk); an edit never closes another version.</param>
    public static RatePlacement Place(IReadOnlyList<RateSpan> existing, DateOnly from, DateOnly? to, Guid? exceptId, bool closeOpenEnded)
    {
        var others = existing.Where(v => v.Id != exceptId).ToList();
        var closes = closeOpenEnded
            ? others.Where(v => v.ValidTo is null && v.ValidFrom < from).OrderByDescending(v => v.ValidFrom).FirstOrDefault()
            : null;

        foreach (var version in others.OrderBy(v => v.ValidFrom))
        {
            var end = version == closes ? from.AddDays(-1) : version.ValidTo;
            if (version.ValidFrom <= (to ?? DateOnly.MaxValue) && (end ?? DateOnly.MaxValue) >= from)
            {
                return new RatePlacement(version.Id, null, null);
            }
        }

        return closes is null ? new RatePlacement(null, null, null) : new RatePlacement(null, closes.Id, from.AddDays(-1));
    }
}
