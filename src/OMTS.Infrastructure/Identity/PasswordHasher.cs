using Microsoft.AspNetCore.Identity;
using OMTS.Application.Common.Interfaces.Identity;
using OMTS.Application.Common.Models;
using OMTS.Domain.Entities.Identity;

namespace OMTS.Infrastructure.Identity;

/// <summary>ASP.NET Core Identity's PBKDF2 hasher (salted, versioned format) without the rest of Identity.</summary>
internal sealed class PasswordHasher : IPasswordHasher
{
    private readonly PasswordHasher<Account> _inner = new();

    public string Hash(string password) => _inner.HashPassword(null!, password);

    public PasswordCheck Verify(string passwordHash, string password)
    {
        if (string.IsNullOrEmpty(passwordHash))
        {
            return PasswordCheck.Failed;
        }

        return _inner.VerifyHashedPassword(null!, passwordHash, password) switch
        {
            PasswordVerificationResult.Success => PasswordCheck.Success,
            PasswordVerificationResult.SuccessRehashNeeded => PasswordCheck.SuccessRehashNeeded,
            _ => PasswordCheck.Failed,
        };
    }
}
