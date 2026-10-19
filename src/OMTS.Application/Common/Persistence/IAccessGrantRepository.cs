using OMTS.Domain.Entities;

namespace OMTS.Application.Common.Persistence;

/// <summary>An effective zone–role pair with its role's permissions; <see cref="ZoneId"/> null means All zones.</summary>
public sealed record EffectiveGrant(Guid? ZoneId, Guid RoleId, string RoleName, IReadOnlyList<RolePermission> Permissions);

/// <summary>
/// "Effective" means status Effective, within its validity period on <c>today</c>, and the role still
/// In Use (BR-19); revoked or expired grants are never returned.
/// </summary>
public interface IAccessGrantRepository
{
    Task<bool> HasEffectiveGrantAsync(Guid accountId, DateOnly today, CancellationToken cancellationToken);

    Task<IReadOnlyList<EffectiveGrant>> GetEffectiveGrantsAsync(Guid accountId, DateOnly today, CancellationToken cancellationToken);
}
