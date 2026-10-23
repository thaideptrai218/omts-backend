namespace OMTS.Application.Common.Interfaces.Identity;

/// <summary>
/// Keyed hash of a 6-digit verification code (ACS-06). Keyed with a server secret, so a copy of the
/// database alone does not reveal the codes (an unkeyed hash of 10^6 values is reversed instantly).
/// </summary>
public interface IVerificationCodeHasher
{
    string Hash(Guid requestId, string code);

    /// <summary>Constant-time comparison with a stored hash.</summary>
    bool Matches(Guid requestId, string code, string storedHash);
}
