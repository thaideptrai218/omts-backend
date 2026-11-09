using OMTS.Domain.Entities.Identity;

namespace OMTS.Application.Common.Interfaces.Persistence;

public interface IAccountRepository
{
    Task<Account?> GetByIdAsync(Guid accountId, CancellationToken cancellationToken);

    /// <summary>Display names of the accounts that exist among <paramref name="accountIds"/>.</summary>
    Task<IReadOnlyDictionary<Guid, string>> GetDisplayNamesAsync(IReadOnlyCollection<Guid> accountIds, CancellationToken cancellationToken);

    /// <summary>
    /// Account whose username is <paramref name="username"/>, or whose phone number equals the normalised
    /// <paramref name="phoneNumber"/>, verified or not: the password proves the identity, and an unverified phone then
    /// meets the phone-verification gate (ACS-02). An email address never matches.
    /// </summary>
    Task<Account?> FindByLoginIdentifierAsync(string username, string? phoneNumber, CancellationToken cancellationToken);

    Task<bool> UsernameExistsAsync(string username, Guid? exceptAccountId, CancellationToken cancellationToken);

    Task<Account?> FindByUsernameAsync(string username, CancellationToken cancellationToken);

    /// <summary>Any account using this phone number, verified or not (login phone numbers are unique system-wide).</summary>
    Task<Account?> FindByPhoneAsync(string phoneNumber, CancellationToken cancellationToken);

    /// <summary>Trader accounts in <paramref name="statusId"/> (Pending approval or Rejected), oldest request first.</summary>
    Task<IReadOnlyList<Account>> ListTraderRequestsAsync(int statusId, CancellationToken cancellationToken);

    /// <summary>The account linked to this Zalo user (Zalo Mini App "Liên kết tài khoản").</summary>
    Task<Account?> FindByZaloAccountIdAsync(string zaloAccountId, CancellationToken cancellationToken);

    /// <summary>Any account using this email, verified or not (emails are unique system-wide, BR-ACCOUNT-02).</summary>
    Task<Account?> FindByEmailAsync(string email, CancellationToken cancellationToken);

    /// <summary>Staff accounts by name, username or phone (contains), optional status, ordered by display name.</summary>
    Task<(IReadOnlyList<Account> Items, int Total)> SearchStaffAsync(
        string? search, int? statusId, int page, int pageSize, CancellationToken cancellationToken);

    /// <summary>The Active Cooperative Owner accounts, for a notification that has no zone (BR-LABOR-04).</summary>
    Task<IReadOnlyList<Guid>> ListActiveOwnerIdsAsync(CancellationToken cancellationToken);

    void Add(Account account);
}
