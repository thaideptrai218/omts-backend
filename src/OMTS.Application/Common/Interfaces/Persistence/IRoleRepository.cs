using OMTS.Domain.Entities.Identity;

namespace OMTS.Application.Common.Interfaces.Persistence;

public interface IRoleRepository
{
    /// <summary>Role with its permission rows.</summary>
    Task<Role?> GetByIdAsync(Guid roleId, CancellationToken cancellationToken);

    Task<Role?> FindByNameAsync(string name, CancellationToken cancellationToken);

    /// <summary>Roles with their permission rows, optionally of one status, ordered by name.</summary>
    Task<IReadOnlyList<Role>> ListAsync(int? statusId, CancellationToken cancellationToken);

    void Add(Role role);

    void AddPermission(RolePermission permission);
}
