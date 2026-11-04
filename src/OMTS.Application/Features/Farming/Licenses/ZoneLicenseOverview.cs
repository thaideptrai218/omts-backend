using OMTS.Application.Common;
using OMTS.Application.Common.Interfaces.Persistence;

namespace OMTS.Application.Features.Farming.Licenses;

/// <summary>One license of a zone with its state on the day asked for.</summary>
public sealed record ZoneLicenseRow(LicenseWithReplacement License, LicenseState State);

/// <summary>FARM-03 licenses of a zone as of a date, and the newest license that the zone list badge and overview show.</summary>
public static class ZoneLicenseOverview
{
    /// <summary>The licenses of <paramref name="zoneId"/> with their state on <paramref name="day"/>, newest first.</summary>
    public static IReadOnlyList<ZoneLicenseRow> For(Guid zoneId, IEnumerable<LicenseWithReplacement> licenses, DateOnly day) =>
        NewestFirst(licenses.Where(l => l.Document.ZoneId == zoneId))
            .Select(l => new ZoneLicenseRow(l, LicenseStateRules.StateOn(Facts(l), day)))
            .ToList();

    /// <summary>
    /// The newest license of the zone that is not Replaced: the latest effective date, then the latest recorded. For display
    /// only: its state is the badge of the zone. GFR-03 reminds every license that is not Replaced, not only this one.
    /// </summary>
    public static LicenseWithReplacement? Newest(Guid zoneId, IEnumerable<LicenseWithReplacement> licenses) =>
        NewestFirst(licenses.Where(l => l.Document.ZoneId == zoneId && !l.IsReplaced)).FirstOrDefault();

    /// <summary>The zone list badge: Expired or Expiring soon of the newest license today; null when it is fine or there is none.</summary>
    public static LicenseState? BadgeFor(Guid zoneId, IEnumerable<LicenseWithReplacement> licenses, DateOnly today)
    {
        var state = Newest(zoneId, licenses) is { } newest ? LicenseStateRules.StateOn(Facts(newest), today) : (LicenseState?)null;
        return state is LicenseState.Expired or LicenseState.ExpiringSoon ? state : null;
    }

    /// <summary>The dates of the license and the business date its replacement was recorded (BR-FARM-06).</summary>
    public static LicenseFacts Facts(LicenseWithReplacement license) => new(
        license.Document.EffectiveDate,
        license.Document.ExpiryDate,
        license.ReplacedAt is { } replacedAt ? BusinessClock.ToBusinessDate(replacedAt) : null);

    private static IOrderedEnumerable<LicenseWithReplacement> NewestFirst(IEnumerable<LicenseWithReplacement> licenses) => licenses
        .OrderByDescending(l => l.Document.EffectiveDate)
        .ThenByDescending(l => l.Document.CreatedAt)
        .ThenByDescending(l => l.Document.Id);
}
