using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using OMTS.Application.Common.Interfaces.Identity;
using OMTS.Application.Common.Models;
using OMTS.Application.Common.Validation;
using OMTS.Infrastructure.Persistence;

namespace OMTS.Infrastructure.Bootstrap.Demo;

/// <summary>
/// The one place that decides whether <c>seed --bootstrap --demo</c> may run here, and with which password. Called
/// before anything is written. Development: yes, with the well-known password of the README. Staging (a shared demo
/// server): only with <see cref="AllowOutsideDevelopmentKey"/>, a secret <see cref="PasswordKey"/> that is not
/// well-known, and a database where no account holds the well-known one. Production and any other environment name:
/// never, whatever the configuration says. Refusals name the keys, never a value.
/// </summary>
internal static class DemoSeedGate
{
    public const string AllowOutsideDevelopmentKey = "Demo:AllowOutsideDevelopment";
    public const string PasswordKey = "Demo:Password";

    /// <summary>The demo password of local Development, printed in the README. Refused everywhere else.</summary>
    public const string WellKnownPassword = "Demo@1234";

    private const string Guide = "see README \"Demo on a shared server\"";

    public static (string? Password, string? Refusal) Check(IHostEnvironment environment, IConfiguration configuration)
    {
        if (environment.IsDevelopment())
        {
            return (WellKnownPassword, null);
        }

        if (!environment.IsStaging())
        {
            return (null, "Demo data is refused outside Development and Staging; no setting opens it in this environment.");
        }

        // Anything but a plain true (unset, "1", "yes") stays closed.
        if (!bool.TryParse(configuration[AllowOutsideDevelopmentKey], out var allowed) || !allowed)
        {
            return (null, $"Demo data is refused in Staging unless {AllowOutsideDevelopmentKey} is true; {Guide}.");
        }

        var password = configuration[PasswordKey];
        return PasswordProblem(password) is { } problem
            ? (null, $"Demo data is refused in Staging: {PasswordKey} {problem}; {Guide}.")
            : (password, null);
    }

    /// <summary>
    /// Outside Development, a database whose accounts hold the well-known password is refused: the seed never resets a
    /// password it set, so a database first seeded in Development would keep it on a shared server. Every account is
    /// checked, so a later demo step cannot be forgotten here. Only reads, and only what every migration has: this runs
    /// before the migrations, on a database that may be missing, empty or behind.
    /// </summary>
    public static async Task<string?> DatabaseRefusalAsync(
        IHostEnvironment environment, OmtsDbContext db, IPasswordHasher hasher, CancellationToken cancellationToken)
    {
        if (environment.IsDevelopment())
        {
            return null;
        }

        // No migration applied, also when the database does not exist yet: there is no account table to read.
        if (db.Database.IsRelational() && !(await db.Database.GetAppliedMigrationsAsync(cancellationToken)).Any())
        {
            return null;
        }

        var accounts = await db.Accounts.AsNoTracking().Select(a => new { a.Username, a.PasswordHash }).ToListAsync(cancellationToken);
        var holding = accounts
            .Where(a => hasher.Verify(a.PasswordHash, WellKnownPassword) != PasswordCheck.Failed)
            .Select(a => a.Username)
            .Order()
            .ToList();
        return holding.Count == 0
            ? null
            : $"Demo data is refused in Staging: these accounts hold the well-known password of the local demo: {string.Join(", ", holding)}. " +
              $"Start again from an empty database; {Guide}.";
    }

    private static string? PasswordProblem(string? password)
    {
        if (string.IsNullOrWhiteSpace(password))
        {
            return "is not set";
        }

        // A stray newline from a secret store would leave nobody able to log in.
        if (password != password.Trim())
        {
            return "must not start or end with whitespace";
        }

        if (string.Equals(password, WellKnownPassword, StringComparison.OrdinalIgnoreCase))
        {
            return "must not be the well-known password of the local demo";
        }

        return PasswordPolicy.IsValid(password)
            ? null
            : $"must have {PasswordPolicy.MinimumLength} to {PasswordPolicy.MaximumLength} characters with a letter and a digit";
    }
}
