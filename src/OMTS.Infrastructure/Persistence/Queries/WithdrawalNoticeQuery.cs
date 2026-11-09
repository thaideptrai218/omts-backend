using Microsoft.EntityFrameworkCore;
using OMTS.Application.Common.Interfaces.Persistence;
using OMTS.Application.Features.Harvest;
using OMTS.Application.Features.Notifications;
using OMTS.Application.Features.Traceability.Withdrawal;
using OMTS.Domain.Entities.Platform;
using OMTS.Domain.Enums;

namespace OMTS.Infrastructure.Persistence.Queries;

/// <summary>TRACE-04 step 7 read again: the latest withdrawal audit row of a lot and the sendings it names. One query each.</summary>
internal sealed class WithdrawalNoticeQuery(OmtsDbContext db) : IWithdrawalNoticeQuery
{
    public Task<string?> LatestWithdrawalAsync(Guid lotId, CancellationToken cancellationToken) =>
        db.AuditEvents.AsNoTracking()
            .Where(e => e.RecordType == HarvestLotAudit.RecordType && e.RecordId == lotId && e.Action == LotWithdrawalService.WithdrawnAction)
            .OrderByDescending(e => e.OccurredAt)
            .Select(e => e.After)
            .FirstOrDefaultAsync(cancellationToken);

    public async Task<IReadOnlyList<Notification>> SendingsAsync(Guid lotId, IReadOnlyCollection<Guid> sendingIds, CancellationToken cancellationToken)
    {
        var ids = sendingIds.Distinct().ToList();
        return ids.Count == 0
            ? []
            : await db.Notifications.AsNoTracking()
                .Where(n => ids.Contains(n.Id) && n.RelatedRecordId == lotId && n.Type == NotificationTypes.ProvenanceWithdrawn && n.Channel != NotificationChannel.InApp)
                .ToListAsync(cancellationToken);
    }
}
