using OMTS.Domain.Entities.Identity;
using OMTS.Domain.Enums;

namespace OMTS.Application.Common.Interfaces.Persistence;

public interface IVerificationCodeRepository
{
    void Add(VerificationCode code);

    /// <summary>Removes a request whose code could not be sent.</summary>
    void Remove(VerificationCode code);

    /// <summary>When codes (first sends and resends) went out to the target after the time, oldest first, over all accounts and purposes.</summary>
    Task<IReadOnlyList<DateTimeOffset>> GetSendTimesToTargetAsync(string target, DateTimeOffset since, CancellationToken cancellationToken);

    /// <summary>SMS codes sent since the time, system-wide.</summary>
    Task<int> CountSmsSendsAsync(DateTimeOffset since, CancellationToken cancellationToken);

    /// <summary>
    /// When codes went out after <paramref name="since"/> for one purpose and one target, oldest first, over all
    /// requests and accounts (BR-ACCOUNT-09), read fresh from the store so that parallel sends see each other.
    /// </summary>
    Task<IReadOnlyList<DateTimeOffset>> GetSendTimesAsync(VerificationPurpose purpose, string target, DateTimeOffset since, CancellationToken cancellationToken);

    Task<VerificationCode?> GetByIdAsync(Guid id, CancellationToken cancellationToken);

    Task<VerificationCode?> GetByTokenHashAsync(string tokenHash, CancellationToken cancellationToken);

    /// <summary>
    /// Requests of a purpose created since <paramref name="since"/>, newest first, in any state, for one
    /// subject: the account (any target), or without an account (registration) the target. Limits that
    /// must hold across requests and delivery methods are counted on these.
    /// </summary>
    Task<IReadOnlyList<VerificationCode>> GetRecentAsync(
        VerificationPurpose purpose, Guid? accountId, string target, DateTimeOffset since, CancellationToken cancellationToken);

    /// <summary>Requests of one purpose and one target, however old and whoever asked, whose code can still be entered.</summary>
    Task<IReadOnlyList<VerificationCode>> ListOpenAsync(VerificationPurpose purpose, string target, DateTimeOffset now, CancellationToken cancellationToken);

    /// <summary>Requests of the account on one channel whose code or token can still be used.</summary>
    Task<IReadOnlyList<VerificationCode>> ListUsableOfAccountAsync(Guid accountId, VerificationChannel channel, DateTimeOffset now, CancellationToken cancellationToken);
}
