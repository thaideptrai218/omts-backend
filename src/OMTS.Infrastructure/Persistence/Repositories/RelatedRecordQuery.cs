using Microsoft.EntityFrameworkCore;
using OMTS.Application.Common.Interfaces.Persistence;
using OMTS.Application.Features.Notifications;

namespace OMTS.Infrastructure.Persistence.Repositories;

/// <summary>The related records notifications point to (<see cref="RelatedRecordTypes"/>); a module that adds a type adds its case.</summary>
internal sealed class RelatedRecordQuery(OmtsDbContext db) : IRelatedRecordQuery
{
    public async Task<RelatedRecordLocation?> FindAsync(string recordType, Guid recordId, CancellationToken cancellationToken)
    {
        switch (recordType)
        {
            case RelatedRecordTypes.WaterMeasurement:
                var zoneIds = await db.WaterMeasurements.AsNoTracking().Where(m => m.Id == recordId).Select(m => m.ZoneId).ToListAsync(cancellationToken);
                return zoneIds.Count == 0 ? null : new RelatedRecordLocation(zoneIds[0]);
            case RelatedRecordTypes.FarmingZone:
                return await db.FarmingZones.AnyAsync(z => z.Id == recordId, cancellationToken) ? new RelatedRecordLocation(recordId) : null;
            // Owner-only records: their zones are not checked.
            case RelatedRecordTypes.ZoneLicense:
                return await db.ZoneComplianceDocuments.AnyAsync(d => d.Id == recordId, cancellationToken) ? new RelatedRecordLocation(null) : null;
            case RelatedRecordTypes.Account:
                return await db.Accounts.AnyAsync(a => a.Id == recordId, cancellationToken) ? new RelatedRecordLocation(null) : null;
            default:
                return null;
        }
    }
}
