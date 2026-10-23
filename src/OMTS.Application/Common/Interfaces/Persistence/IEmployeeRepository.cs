using OMTS.Domain.Entities.Identity;

namespace OMTS.Application.Common.Interfaces.Persistence;

public interface IEmployeeRepository
{
    Task<Employee?> GetByIdAsync(Guid id, CancellationToken cancellationToken);

    Task<Employee?> GetByAccountIdAsync(Guid accountId, CancellationToken cancellationToken);

    /// <summary>Employee id per linked account, for the accounts that have one.</summary>
    Task<IReadOnlyDictionary<Guid, Guid?>> GetIdsByAccountIdsAsync(IReadOnlyCollection<Guid> accountIds, CancellationToken cancellationToken);
}
