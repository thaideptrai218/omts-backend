using OMTS.Application.Common.Models.Persistence;
using OMTS.Application.Features.Farming.Licenses.Models;
using OMTS.Application.Common.Interfaces.Persistence;
using OMTS.Domain.Entities.Farming;

namespace OMTS.Application.Features.Farming.Licenses;

/// <summary>FARM-03 zone license overview as of a date.</summary>
public static class ZoneLicenseOverview
{
    /// <summary>
    /// Every document covering <paramref name="zoneId"/> with its state on <paramref name="day"/>, plus a
    /// Missing row for each mandatory type that has no Valid or Expiring soon document that day.
    /// </summary>
    public static IReadOnlyList<ZoneLicenseRow> For(Guid zoneId, IEnumerable<LicenseWithReplacements> licenses, DateOnly day)
    {
        var rows = licenses
            .Where(l => l.Document.ZoneScopes.Any(s => s.ZoneId == zoneId))
            .Select(l => new ZoneLicenseRow(l.Document.DocumentType, l.Document, l.IsReplaced, LicenseStateRules.StateOn(Facts(l), day)))
            .OrderBy(r => r.DocumentType).ThenByDescending(r => r.Document!.EffectiveDate)
            .ToList();

        foreach (var type in LicenseTypes.Mandatory)
        {
            if (!rows.Any(r => r.DocumentType == type && r.State is LicenseState.Valid or LicenseState.ExpiringSoon))
            {
                rows.Add(new ZoneLicenseRow(type, null, false, LicenseState.Missing));
            }
        }

        return rows;
    }

    /// <summary>The zone list badge: Expired, then Expiring soon, then Missing; null when all is fine.</summary>
    public static LicenseState? WorstFor(Guid zoneId, IEnumerable<LicenseWithReplacements> licenses, DateOnly day) =>
        LicenseStateRules.Worst(For(zoneId, licenses, day)
            // A replaced document's own expiry is no longer a problem of the zone.
            .Where(r => r.State != LicenseState.Superseded && !(r.IsReplaced && r.State == LicenseState.Expired))
            .Select(r => r.State));

    public static LicenseFacts Facts(LicenseWithReplacements license) =>
        new(license.Document.EffectiveDate, license.Document.ExpiryDate, license.ReplacementEffectiveDates);
}
