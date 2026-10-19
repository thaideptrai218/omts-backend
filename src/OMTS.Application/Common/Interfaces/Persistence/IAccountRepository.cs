using OMTS.Domain.Entities.Identity;

namespace OMTS.Application.Common.Interfaces.Persistence;

public interface IAccountRepository
{
    Task<Account?> GetByIdAsync(Guid accountId, CancellationToken cancellationToken);

    /// <summary>Account matching a username, a verified phone number or a verified email (UC02).</summary>
    Task<Account?> FindByLoginIdentifierAsync(string identifier, CancellationToken cancellationToken);

    void Add(Account account);
}
