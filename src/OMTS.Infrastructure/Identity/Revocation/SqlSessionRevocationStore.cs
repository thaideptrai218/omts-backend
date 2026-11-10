using OMTS.Application.Common.Interfaces.Identity;

namespace OMTS.Infrastructure.Identity.Revocation;

/// <summary>No revocation store configured: every request is checked against the database.</summary>
internal sealed class SqlSessionRevocationStore : ISessionRevocationStore
{
    public ValueTask<SessionRevocationStatus> GetStatusAsync(Guid sessionId) => ValueTask.FromResult(SessionRevocationStatus.Unknown);

    public Task<bool> TryMarkRevokedAsync(IReadOnlyCollection<RevokedSession> sessions) => Task.FromResult(true);
}
