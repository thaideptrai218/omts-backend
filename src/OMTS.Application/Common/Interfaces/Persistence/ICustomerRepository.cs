using OMTS.Domain.Entities.Sales;

namespace OMTS.Application.Common.Interfaces.Persistence;

public interface ICustomerRepository
{
    /// <summary>Offline profiles (no account, not merged, In Use) with this phone number.</summary>
    Task<IReadOnlyList<Customer>> GetOfflineByPhoneAsync(string phoneNumber, CancellationToken cancellationToken);

    Task<Customer?> GetByAccountIdAsync(Guid accountId, CancellationToken cancellationToken);

    void Add(Customer customer);
}
