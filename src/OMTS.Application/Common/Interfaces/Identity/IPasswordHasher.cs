using OMTS.Application.Common.Models;

namespace OMTS.Application.Common.Interfaces.Identity;

/// <summary>Salted one-way password hashing (NFR-01).</summary>
public interface IPasswordHasher
{
    string Hash(string password);
    PasswordCheck Verify(string passwordHash, string password);
}
