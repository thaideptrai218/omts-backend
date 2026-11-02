using OMTS.Application.Common.Interfaces.Persistence;
using OMTS.Domain.Entities.Platform;

namespace OMTS.Infrastructure.Persistence.Repositories.Platform;

internal sealed class AuditEventRepository(OmtsDbContext db) : IAuditEventRepository
{
    public void Add(AuditEvent auditEvent) => db.AuditEvents.Add(auditEvent);
}
