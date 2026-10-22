using OMTS.Application.Common.Interfaces;
using OMTS.Application.Common.Interfaces.Persistence;
using OMTS.Application.Features.Audit.Models;
using OMTS.Domain.Entities.Platform;

namespace OMTS.Application.Features.Audit;

/// <summary>
/// Writes the audit log (E48): actor, time, action, record, before/after values and reason (NFR-02).
/// Used by every create/update/status change of ACS, FARM, ENV, INP, LAB and the deployment bootstrap.
/// The log is never edited or deleted and is kept at least 5 years: no clean-up job may touch it.
/// </summary>
public sealed class AuditLogService(
    IAuditEventRepository auditEvents,
    ICurrentUser currentUser,
    IStatusCatalog statusCatalog,
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

    /// <summary>
    /// Records a life-cycle status transition the same way in every module: action
    /// <c>{recordType}.StatusChanged</c> with the old and new status codes, and the reason when the
    /// transition requires one.
    /// </summary>
    public AuditEvent RecordStatusChange(
        string recordType,
        Guid recordId,
        string? recordCode,
        int fromStatusId,
        int toStatusId,
        string? reason = null,
        Guid? zoneId = null,
        Guid? actorAccountId = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(recordType);
        if (fromStatusId == toStatusId)
        {
            throw new ArgumentException("A status change needs two different statuses.", nameof(toStatusId));
        }

        return Record(new AuditEntry($"{recordType}.StatusChanged", recordType)
        {
            RecordId = recordId,
            RecordCode = recordCode,
            Before = new { Status = statusCatalog.CodeOf(fromStatusId) },
            After = new { Status = statusCatalog.CodeOf(toStatusId) },
            Reason = reason,
            ZoneId = zoneId,
            ActorAccountId = actorAccountId,
        });
    }
}
