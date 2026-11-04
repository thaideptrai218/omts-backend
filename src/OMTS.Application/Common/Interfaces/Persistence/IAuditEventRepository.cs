using OMTS.Domain.Entities.Platform;

namespace OMTS.Application.Common.Interfaces.Persistence;

/// <summary>Append-only: audit events are never edited or deleted (E48, NFR-02).</summary>
public interface IAuditEventRepository
{
    void Add(AuditEvent auditEvent);

    /// <summary>Whether this database ever audited a record of the type: the rows outlive the record.</summary>
    Task<bool> AnyOfRecordTypeAsync(string recordType, CancellationToken cancellationToken);
}
