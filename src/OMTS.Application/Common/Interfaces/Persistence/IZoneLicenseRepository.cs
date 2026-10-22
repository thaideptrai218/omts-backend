using OMTS.Domain.Entities.Farming;

namespace OMTS.Application.Common.Interfaces.Persistence;

/// <summary>A license with the effective dates of the documents that replace it (FARM-03 Superseded rule).</summary>
public sealed record LicenseWithReplacements(ZoneComplianceDocument Document, IReadOnlyList<DateOnly?> ReplacementEffectiveDates, bool IsReplaced);

public interface IZoneLicenseRepository
{
    /// <summary>License with its zone scopes (and zones), tracked for editing.</summary>
    Task<ZoneComplianceDocument?> GetForEditAsync(Guid documentId, CancellationToken cancellationToken);

    /// <summary>The document that replaces this one, if any.</summary>
    Task<ZoneComplianceDocument?> FindReplacementAsync(Guid documentId, CancellationToken cancellationToken);

    /// <summary>Every license covering any of the zones, with scopes and replacements, untracked.</summary>
    Task<IReadOnlyList<LicenseWithReplacements>> ListCoveringAsync(IReadOnlyCollection<Guid> zoneIds, CancellationToken cancellationToken);

    /// <summary>Licenses with an expiry date that have not been replaced, with zone scopes (reminder job), tracked.</summary>
    Task<IReadOnlyList<ZoneComplianceDocument>> ListReminderCandidatesAsync(CancellationToken cancellationToken);

    void Add(ZoneComplianceDocument document);

    void AddScope(DocumentZoneScope scope);

    void RemoveScope(DocumentZoneScope scope);
}
