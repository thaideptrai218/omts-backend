using OMTS.Application.Features.Authorization.Models;
using OMTS.Domain.Entities.Identity;

namespace OMTS.Application.Common.Interfaces.Persistence;

/// <summary>
/// Zone–role pairs (FARM-05). A pair is Effective until it is revoked (Effective / Revoked only). Only Effective pairs of
/// a fixed role count for permissions, login and zone visibility: revoked pairs never do, nor pairs of a role row that is
/// not a fixed role (an old Owner-made role kept by the FixedRoles migration).
/// </summary>
public interface IAccessGrantRepository
{
    /// <summary>Whether the account holds an Effective pair of a fixed role (staff log in only then).</summary>
    Task<bool> HasEffectiveGrantAsync(Guid accountId, CancellationToken cancellationToken);

    /// <summary>The Effective pairs of a fixed role of the account, each with the permissions of its role (FixedRoles).</summary>
    Task<IReadOnlyList<ZoneGrant>> GetEffectiveGrantsAsync(Guid accountId, CancellationToken cancellationToken);

    /// <summary>Every pair of the account (revoked and old-role ones included), with role and zone loaded, newest first.</summary>
    Task<IReadOnlyList<AccessGrant>> ListForAccountAsync(Guid accountId, CancellationToken cancellationToken);

    /// <summary>The Effective pairs of a fixed role of the account, with role and zone loaded, newest first.</summary>
    Task<IReadOnlyList<AccessGrant>> ListEffectiveForAccountAsync(Guid accountId, CancellationToken cancellationToken);

    Task<AccessGrant?> GetAsync(Guid grantId, Guid accountId, CancellationToken cancellationToken);

    /// <summary>
    /// An Effective pair of the account with this role in this zone or in All zones (BR-FARM-08 (A): a pair for one
    /// zone duplicates a pair of the same role for All zones). For an All zones request, only an All zones pair counts.
    /// </summary>
    Task<AccessGrant?> FindDuplicateAsync(Guid accountId, Guid roleId, Guid? zoneId, CancellationToken cancellationToken);

    void Add(AccessGrant grant);
}
