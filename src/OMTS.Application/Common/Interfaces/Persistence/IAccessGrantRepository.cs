using OMTS.Application.Common.Models;

namespace OMTS.Application.Common.Interfaces.Persistence;

/// <summary>
/// "Effective" means status Effective, within its validity period on <c>today</c>, and the role still
/// In Use (BR-19); revoked or expired grants are never returned.
/// </summary>
public interface IAccessGrantRepository
{
    Task<bool> HasEffectiveGrantAsync(Guid accountId, DateOnly today, CancellationToken cancellationToken);

    Task<IReadOnlyList<EffectiveGrant>> GetEffectiveGrantsAsync(Guid accountId, DateOnly today, CancellationToken cancellationToken);
}
