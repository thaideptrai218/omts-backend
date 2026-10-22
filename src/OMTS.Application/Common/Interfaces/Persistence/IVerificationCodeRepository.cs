using OMTS.Domain.Entities.Identity;
using OMTS.Domain.Enums;

namespace OMTS.Application.Common.Interfaces.Persistence;

public interface IVerificationCodeRepository
{
    void Add(VerificationCode code);

    Task<VerificationCode?> GetByIdAsync(Guid id, CancellationToken cancellationToken);

    Task<VerificationCode?> GetByTokenHashAsync(string tokenHash, CancellationToken cancellationToken);

    /// <summary>
    /// Requests of a purpose that are still open (not verified, not cancelled), newest first: the
    /// account's requests for any target, or, without an account (registration), those for the target.
    /// </summary>
    Task<IReadOnlyList<VerificationCode>> GetOpenAsync(
        VerificationPurpose purpose, Guid? accountId, string target, CancellationToken cancellationToken);
}
