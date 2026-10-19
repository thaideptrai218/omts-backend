using OMTS.Domain.Entities.Platform;

namespace OMTS.Application.Common.Interfaces.Persistence;

/// <summary>Append-only: audit events are never edited or deleted (E48, NFR-02).</summary>
public interface IAuditEventRepository
{
    void Add(AuditEvent auditEvent);
}
