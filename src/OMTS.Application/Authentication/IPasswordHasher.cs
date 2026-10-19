namespace OMTS.Application.Authentication;

public enum PasswordCheck
{
    Failed,
    Success,

    /// <summary>Correct, but hashed with outdated parameters; a new hash should be stored.</summary>
    SuccessRehashNeeded,
}

/// <summary>Salted one-way password hashing (NFR-01).</summary>
public interface IPasswordHasher
{
    string Hash(string password);
    PasswordCheck Verify(string passwordHash, string password);
}
