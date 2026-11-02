using Microsoft.EntityFrameworkCore;
using OMTS.Application.Common.Interfaces.Persistence;
using OMTS.Application.Common.Models.Persistence;
using OMTS.Domain.Entities.Farming;

namespace OMTS.Infrastructure.Persistence.Repositories.Farming;

internal sealed class ZoneLicenseRepository(OmtsDbContext db) : IZoneLicenseRepository
{
    public Task<ZoneComplianceDocument?> GetForEditAsync(Guid documentId, CancellationToken cancellationToken) =>
        db.ZoneComplianceDocuments
            .Include(d => d.ZoneScopes).ThenInclude(s => s.Zone)
            .FirstOrDefaultAsync(d => d.Id == documentId, cancellationToken);

    public Task<ZoneComplianceDocument?> FindReplacementAsync(Guid documentId, CancellationToken cancellationToken) =>
        db.ZoneComplianceDocuments.AsNoTracking().FirstOrDefaultAsync(d => d.ReplacesDocumentId == documentId, cancellationToken);

    public async Task<IReadOnlyList<LicenseWithReplacements>> ListCoveringAsync(IReadOnlyCollection<Guid> zoneIds, CancellationToken cancellationToken)
    {
        var documents = await db.ZoneComplianceDocuments.AsNoTracking()
            .Include(d => d.ZoneScopes).ThenInclude(s => s.Zone)
            .Where(d => d.ZoneScopes.Any(s => zoneIds.Contains(s.ZoneId)))
            .ToListAsync(cancellationToken);

        var ids = documents.Select(d => d.Id).ToList();
        var replacements = (await db.ZoneComplianceDocuments.AsNoTracking()
                .Where(d => d.ReplacesDocumentId != null && ids.Contains(d.ReplacesDocumentId.Value))
                .Select(d => new { ReplacedId = d.ReplacesDocumentId!.Value, d.EffectiveDate })
                .ToListAsync(cancellationToken))
            .ToLookup(r => r.ReplacedId, r => r.EffectiveDate);

        return documents
            .Select(d => new LicenseWithReplacements(d, replacements[d.Id].ToList(), replacements[d.Id].Any()))
            .ToList();
    }

    public async Task<IReadOnlyList<Guid>> ListReminderCandidateIdsAsync(DateOnly today, int expiringSoonDays, CancellationToken cancellationToken)
    {
        var soon = today.AddDays(expiringSoonDays);
        return await db.ZoneComplianceDocuments.AsNoTracking()
            .Where(d => d.ExpiryDate != null && d.ExpiryDate <= soon
                && (d.ExpiringReminderSentAt == null || (d.ExpiryDate < today && d.ExpiredReminderSentAt == null))
                && !db.ZoneComplianceDocuments.Any(r => r.ReplacesDocumentId == d.Id))
            .Select(d => d.Id)
            .ToListAsync(cancellationToken);
    }

    public void Add(ZoneComplianceDocument document) => db.ZoneComplianceDocuments.Add(document);

    public void AddScope(DocumentZoneScope scope) => db.DocumentZoneScopes.Add(scope);

    public void RemoveScope(DocumentZoneScope scope) => db.DocumentZoneScopes.Remove(scope);
}
