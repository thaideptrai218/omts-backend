using OMTS.Domain.Entities.Identity;

namespace OMTS.Application.Common.Interfaces.Persistence;

/// <summary>
/// The fixed role rows (BR-FARM-07); read only, they are created at deployment. Rows with another code (old roles kept
/// by the FixedRoles migration) are never returned.
/// </summary>
public interface IRoleRepository
{
    Task<Role?> GetByIdAsync(Guid roleId, CancellationToken cancellationToken);

    Task<Role?> FindByCodeAsync(string code, CancellationToken cancellationToken);

    /// <summary>Every role row, ordered by name.</summary>
    Task<IReadOnlyList<Role>> ListAsync(CancellationToken cancellationToken);
}
