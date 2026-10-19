using OMTS.Application.Common.Interfaces;
using OMTS.Application.Common.Interfaces.Persistence;
using OMTS.Application.Features.Audit.Models;
using OMTS.Domain.Entities.Platform;

namespace OMTS.Application.Features.Audit;

/// <summary>
/// Writes the audit log (E48): actor, time, action, record, before/after values and reason (NFR-02).
/// Used by UC03, UC05, UC09–UC12, UC72 and every group A status transition.
/// </summary>
public sealed class AuditLogService(
    IAuditEventRepository auditEvents,
    ICurrentUser currentUser,
    TimeProvider timeProvider)
{
    /// <summary>
    /// Adds the event to the current unit of work without saving it: the caller's single
    /// <see cref="IUnitOfWork.SaveChangesAsync"/> stores the change and its audit record together, so each
    /// change creates exactly one record and a failed change leaves none.
    /// </summary>
    public AuditEvent Record(AuditEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        ArgumentException.ThrowIfNullOrWhiteSpace(entry.Action);
        ArgumentException.ThrowIfNullOrWhiteSpace(entry.RecordType);

        var auditEvent = new AuditEvent
        {
            ActorAccountId = entry.ActorAccountId ?? currentUser.AccountId,
            Action = entry.Action,
            RecordType = entry.RecordType,
            RecordId = entry.RecordId,
            RecordCode = entry.RecordCode,
            OccurredAt = timeProvider.GetUtcNow(),
            Reason = entry.Reason,
            Before = AuditValueSerializer.Serialize(entry.Before),
            After = AuditValueSerializer.Serialize(entry.After),
            ZoneId = entry.ZoneId,
        };

        auditEvents.Add(auditEvent);
        return auditEvent;
    }
}
