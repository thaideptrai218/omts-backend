using OMTS.Domain.Entities.Identity;

namespace OMTS.Application.Common.Interfaces.Persistence;

public interface IAccountRepository
{
    Task<Account?> GetByIdAsync(Guid accountId, CancellationToken cancellationToken);

    /// <summary>
    /// Account whose username is <paramref name="username"/>, or whose verified phone number / verified
    /// email equals the normalised <paramref name="phoneNumber"/> / <paramref name="email"/> (ACS-02).
    /// </summary>
    Task<Account?> FindByLoginIdentifierAsync(string username, string? phoneNumber, string? email, CancellationToken cancellationToken);

    Task<bool> UsernameExistsAsync(string username, Guid? exceptAccountId, CancellationToken cancellationToken);

    Task<Account?> FindByUsernameAsync(string username, CancellationToken cancellationToken);

    /// <summary>Any account using this phone number, verified or not (login phone numbers are unique system-wide).</summary>
    Task<Account?> FindByPhoneAsync(string phoneNumber, CancellationToken cancellationToken);

    /// <summary>The account whose verified email is this one (verified emails are unique system-wide).</summary>
    Task<Account?> FindByVerifiedEmailAsync(string email, CancellationToken cancellationToken);

    /// <summary>Staff accounts by name, username or phone (contains), optional status, ordered by display name.</summary>
    Task<(IReadOnlyList<Account> Items, int Total)> SearchStaffAsync(
        string? search, int? statusId, int page, int pageSize, CancellationToken cancellationToken);

    /// <summary>Staff accounts not linked to any employee profile (LAB-01 account picker), ordered by display name.</summary>
    Task<IReadOnlyList<Account>> ListLinkableStaffAsync(CancellationToken cancellationToken);

    void Add(Account account);
}
