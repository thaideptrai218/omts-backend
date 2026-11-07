using OMTS.Application.Common.Interfaces.Persistence;
using OMTS.Domain.Entities.Platform;

namespace OMTS.Application.UnitTests.Fakes;

public sealed class FakeAuditEventRepository : IAuditEventRepository
{
    public List<AuditEvent> Added { get; } = [];

    public void Add(AuditEvent auditEvent) => Added.Add(auditEvent);

    public Task<bool> AnyOfRecordTypeAsync(string recordType, CancellationToken cancellationToken) =>
        Task.FromResult(Added.Exists(e => e.RecordType == recordType));
}
