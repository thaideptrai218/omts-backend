using Microsoft.EntityFrameworkCore;
using OMTS.Application.Common.Interfaces.Persistence;
using OMTS.Domain.Constants;
using OMTS.Domain.Entities.Farming;

namespace OMTS.Infrastructure.Persistence.Repositories;

internal sealed class ZoneLicenseRepository(OmtsDbContext db) : IZoneLicenseRepository
{
    public Task<ZoneComplianceDocument?> GetForEditAsync(Guid documentId, CancellationToken cancellationToken) =>
        db.ZoneComplianceDocuments.Include(d => d.Zone).FirstOrDefaultAsync(d => d.Id == documentId, cancellationToken);

    public Task<ZoneComplianceDocument?> FindReplacementAsync(Guid documentId, CancellationToken cancellationToken) =>
        db.ZoneComplianceDocuments.AsNoTracking().FirstOrDefaultAsync(d => d.ReplacesDocumentId == documentId, cancellationToken);

    public async Task<IReadOnlyList<LicenseWithReplacement>> ListForZonesAsync(IReadOnlyCollection<Guid> zoneIds, CancellationToken cancellationToken)
    {
        var documents = await db.ZoneComplianceDocuments.AsNoTracking()
            .Include(d => d.Zone)
            .Where(d => zoneIds.Contains(d.ZoneId))
            .ToListAsync(cancellationToken);

        // A license is replaced at most once (unique ReplacesDocumentId).
        var ids = documents.Select(d => d.Id).ToList();
        var replacedAt = await db.ZoneComplianceDocuments.AsNoTracking()
            .Where(r => r.ReplacesDocumentId != null && ids.Contains(r.ReplacesDocumentId.Value))
            .Select(r => new { ReplacedId = r.ReplacesDocumentId!.Value, r.CreatedAt })
            .ToDictionaryAsync(r => r.ReplacedId, r => r.CreatedAt, cancellationToken);

        return documents.Select(d => new LicenseWithReplacement(d, replacedAt.TryGetValue(d.Id, out var at) ? at : null)).ToList();
    }

    public async Task<IReadOnlyList<Guid>> ListReminderCandidateIdsAsync(DateOnly today, int expiringSoonDays, CancellationToken cancellationToken)
    {
        var soon = today.AddDays(expiringSoonDays);
        return await db.ZoneComplianceDocuments.AsNoTracking()
            .Where(d => d.ExpiryDate != null && d.ExpiryDate <= soon
                && (d.ExpiringReminderSentAt == null || (d.ExpiryDate < today && d.ExpiredReminderSentAt == null))
                && d.Zone.StatusId == StatusIds.Usage.InUse
                && !db.ZoneComplianceDocuments.Any(r => r.ReplacesDocumentId == d.Id))
            .Select(d => d.Id)
            .ToListAsync(cancellationToken);
    }

    public void Add(ZoneComplianceDocument document) => db.ZoneComplianceDocuments.Add(document);
}
