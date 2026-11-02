using OMTS.Application.Common.Models.Persistence;
using OMTS.Domain.Entities.Farming;

namespace OMTS.Application.Common.Interfaces.Persistence;

public interface IZoneLicenseRepository
{
    /// <summary>License with its zone scopes (and zones), tracked for editing.</summary>
    Task<ZoneComplianceDocument?> GetForEditAsync(Guid documentId, CancellationToken cancellationToken);

    /// <summary>The document that replaces this one, if any.</summary>
    Task<ZoneComplianceDocument?> FindReplacementAsync(Guid documentId, CancellationToken cancellationToken);

    /// <summary>Every license covering any of the zones, with scopes and replacements, untracked.</summary>
    Task<IReadOnlyList<LicenseWithReplacements>> ListCoveringAsync(IReadOnlyCollection<Guid> zoneIds, CancellationToken cancellationToken);

    /// <summary>Licenses with an expiry date that have not been replaced, with zone scopes (reminder job), tracked.</summary>
    /// <summary>Ids of unreplaced licenses with a reminder due on <paramref name="today"/> (expiring within the window, or expired, and not yet told).</summary>
    Task<IReadOnlyList<Guid>> ListReminderCandidateIdsAsync(DateOnly today, int expiringSoonDays, CancellationToken cancellationToken);

    void Add(ZoneComplianceDocument document);

    void AddScope(DocumentZoneScope scope);

    void RemoveScope(DocumentZoneScope scope);
}
