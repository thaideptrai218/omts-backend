using Microsoft.EntityFrameworkCore;
using OMTS.Application.Common.Interfaces.Persistence;
using OMTS.Application.Features.Files;

namespace OMTS.Infrastructure.Persistence.Queries;

/// <summary>The one column each cleaned-up file category belongs to.</summary>
internal sealed class StoredFileReferenceQuery(OmtsDbContext db) : IStoredFileReferenceQuery
{
    public async Task<IReadOnlySet<string>> ReferencedKeysAsync(string category, CancellationToken cancellationToken)
    {
        var keys = category switch
        {
            FileCategories.RaftPhotos => db.Rafts.Where(r => r.PhotoUrl != null).Select(r => r.PhotoUrl!),
            FileCategories.ZoneLicenses => db.ZoneComplianceDocuments.Where(d => d.EvidenceFileUrl != null).Select(d => d.EvidenceFileUrl!),
            _ => throw new ArgumentOutOfRangeException(nameof(category), category, "No record column is known for this file category."),
        };

        return (await keys.ToListAsync(cancellationToken)).ToHashSet(StringComparer.Ordinal);
    }
}
