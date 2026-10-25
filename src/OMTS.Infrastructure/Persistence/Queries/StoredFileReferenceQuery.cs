using Microsoft.EntityFrameworkCore;
using OMTS.Application.Common.Interfaces.Persistence;
using OMTS.Application.Features.Files;

namespace OMTS.Infrastructure.Persistence.Queries;

/// <summary>The one column each cleaned-up file category belongs to; read from the database, never from tracked changes.</summary>
internal sealed class StoredFileReferenceQuery(OmtsDbContext db) : IStoredFileReferenceQuery
{
    public async Task<IReadOnlySet<string>> ReferencedKeysAsync(string category, CancellationToken cancellationToken) =>
        (await KeysOf(category).ToListAsync(cancellationToken)).ToHashSet(StringComparer.Ordinal);

    public Task<bool> IsReferencedAsync(string key, CancellationToken cancellationToken) =>
        KeysOf(key.Split('/')[0]).AnyAsync(k => k == key, cancellationToken);

    private IQueryable<string> KeysOf(string category) => category switch
    {
        FileCategories.RaftPhotos => db.Rafts.Where(r => r.PhotoUrl != null).Select(r => r.PhotoUrl!),
        FileCategories.ZoneLicenses => db.ZoneComplianceDocuments.Where(d => d.EvidenceFileUrl != null).Select(d => d.EvidenceFileUrl!),
        _ => throw new ArgumentOutOfRangeException(nameof(category), category, "No record column is known for this file category."),
    };
}
