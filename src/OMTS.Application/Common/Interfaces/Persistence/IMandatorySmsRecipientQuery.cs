using OMTS.Domain.Enums;

namespace OMTS.Application.Common.Interfaces.Persistence;

/// <summary>What a mandatory SMS needs to know about the recipients of a notification.</summary>
public interface IMandatorySmsRecipientQuery
{
    /// <summary>The active accounts among <paramref name="accountIds"/>; one query for all of them.</summary>
    Task<IReadOnlyList<MandatorySmsRecipient>> ListAsync(IReadOnlyCollection<Guid> accountIds, CancellationToken cancellationToken);
}

/// <param name="RoleCodes">The fixed roles of the Effective zone–role pairs of the account.</param>
/// <param name="VerifiedPhoneNumber">The phone number when it is verified (BR-ACCOUNT-03), else null.</param>
public sealed record MandatorySmsRecipient(Guid AccountId, SubjectType SubjectType, IReadOnlyList<string> RoleCodes, string? VerifiedPhoneNumber);
