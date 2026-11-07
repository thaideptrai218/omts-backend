using Microsoft.EntityFrameworkCore;
using OMTS.Application.Common.Interfaces.Persistence;
using OMTS.Domain.Entities.Platform;

namespace OMTS.Infrastructure.Persistence.Repositories;

internal sealed class AuditEventRepository(OmtsDbContext db) : IAuditEventRepository
{
    public void Add(AuditEvent auditEvent) => db.AuditEvents.Add(auditEvent);

    public Task<bool> AnyOfRecordTypeAsync(string recordType, CancellationToken cancellationToken) =>
        db.AuditEvents.AnyAsync(e => e.RecordType == recordType, cancellationToken);
}
