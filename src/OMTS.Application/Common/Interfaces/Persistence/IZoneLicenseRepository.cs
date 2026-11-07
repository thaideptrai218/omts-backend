using OMTS.Domain.Entities.Farming;

namespace OMTS.Application.Common.Interfaces.Persistence;

/// <summary>A license with the time the license replacing it was recorded, if any (BR-FARM-06 Replaced rule).</summary>
public sealed record LicenseWithReplacement(ZoneComplianceDocument Document, DateTimeOffset? ReplacedAt)
{
    public bool IsReplaced => ReplacedAt is not null;
}

public interface IZoneLicenseRepository
{
    /// <summary>License with its zone, tracked for editing.</summary>
    Task<ZoneComplianceDocument?> GetForEditAsync(Guid documentId, CancellationToken cancellationToken);

    /// <summary>The license that replaces this one, if any.</summary>
    Task<ZoneComplianceDocument?> FindReplacementAsync(Guid documentId, CancellationToken cancellationToken);

    /// <summary>Every license of the zones, with its zone and replacement time, untracked.</summary>
    Task<IReadOnlyList<LicenseWithReplacement>> ListForZonesAsync(IReadOnlyCollection<Guid> zoneIds, CancellationToken cancellationToken);

    /// <summary>
    /// Ids of the licenses that may have a reminder due on <paramref name="today"/>: not replaced, of an In Use zone, expiring
    /// within <paramref name="expiringSoonDays"/> days or expired, and not yet told. The caller re-checks each one.
    /// </summary>
    Task<IReadOnlyList<Guid>> ListReminderCandidateIdsAsync(DateOnly today, int expiringSoonDays, CancellationToken cancellationToken);

    void Add(ZoneComplianceDocument document);
}
