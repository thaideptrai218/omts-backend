using OMTS.Domain.Entities.Identity;
using OMTS.Domain.Enums;

namespace OMTS.Application.Common.Interfaces.Persistence;

public interface IVerificationCodeRepository
{
    void Add(VerificationCode code);

    /// <summary>Removes a request whose code could not be sent.</summary>
    void Remove(VerificationCode code);

    /// <summary>Codes sent (first sends and resends) to the target since the time, over all accounts and purposes.</summary>
    Task<int> CountSendsToTargetAsync(string target, DateTimeOffset since, CancellationToken cancellationToken);

    /// <summary>SMS codes sent since the time, system-wide.</summary>
    Task<int> CountSmsSendsAsync(DateTimeOffset since, CancellationToken cancellationToken);

    Task<VerificationCode?> GetByIdAsync(Guid id, CancellationToken cancellationToken);

    Task<VerificationCode?> GetByTokenHashAsync(string tokenHash, CancellationToken cancellationToken);

    /// <summary>
    /// Requests of a purpose created since <paramref name="since"/>, newest first, in any state, for one
    /// subject: the account (any target), or without an account (registration) the target. Limits that
    /// must hold across requests and delivery methods are counted on these.
    /// </summary>
    Task<IReadOnlyList<VerificationCode>> GetRecentAsync(
        VerificationPurpose purpose, Guid? accountId, string target, DateTimeOffset since, CancellationToken cancellationToken);
}
