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
/// well-known, and a database that is empty or its own (<see cref="CheckDatabaseAsync"/>). Production and any other
/// environment name: never, whatever the configuration says. Refusals name the keys, never a value.
/// </summary>
internal static class DemoSeedGate
{
    public const string AllowOutsideDevelopmentKey = "Demo:AllowOutsideDevelopment";
    public const string PasswordKey = "Demo:Password";

    /// <summary>The demo password of local Development, printed in the README. Refused everywhere else.</summary>
    public const string WellKnownPassword = "Demo@1234";

    /// <summary>Audit action that marks a database as seeded by the demo in Staging; written once, by the run that seeds it.</summary>
    public const string DemoSeededAction = "Demo.Seeded";

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
    /// What a bootstrap outside Development may do to this database, for a plain run and a demo run alike. Refused: a
    /// database whose accounts hold the well-known password, since the seed never resets a password it set and a copy
    /// first seeded in Development would keep it on a server. Every account is checked, so a later demo step cannot be
    /// forgotten here. A demo run is also refused on any database with accounts that it did not seed itself:
    /// <see cref="DemoSeededAction"/> marks its own, and <c>MarkNeeded</c> says this run is the one to write it. Only
    /// reads, and only what every migration has: this runs before the migrations, on a database that may be missing,
    /// empty or behind.
    /// </summary>
    public static async Task<(string? Refusal, bool MarkNeeded)> CheckDatabaseAsync(
        IHostEnvironment environment, OmtsDbContext db, IPasswordHasher hasher, bool demo, CancellationToken cancellationToken)
    {
        if (environment.IsDevelopment())
        {
            return (null, false);
        }

        if (!await AccountTableExistsAsync(db, cancellationToken))
        {
            return (null, demo);
        }

        var accounts = await db.Accounts.AsNoTracking().Select(a => new { a.Username, a.PasswordHash }).ToListAsync(cancellationToken);
        var holding = accounts
            .Where(a => hasher.Verify(a.PasswordHash, WellKnownPassword) != PasswordCheck.Failed)
            .Select(a => a.Username)
            .Order()
            .ToList();
        if (holding.Count > 0)
        {
            return ($"Refused in {environment.EnvironmentName}: these accounts hold the well-known password of the local demo: {string.Join(", ", holding)}. " +
                $"Start again from an empty database; {Guide}.", false);
        }

        if (!demo)
        {
            return (null, false);
        }

        var marked = await db.AuditEvents.AnyAsync(e => e.Action == DemoSeededAction, cancellationToken);
        return accounts.Count > 0 && !marked
            ? ($"Demo data is refused in Staging: this database holds accounts and this demo did not seed it. The demo only runs on an empty database or on its own; {Guide}.", false)
            : (null, !marked);
    }

    /// <summary>Not the migration history: a copy that lost it still holds accounts. Missing database: no table.</summary>
    private static async Task<bool> AccountTableExistsAsync(OmtsDbContext db, CancellationToken cancellationToken) =>
        !db.Database.IsRelational()
        || (await db.Database.CanConnectAsync(cancellationToken)
            && await db.Database.SqlQuery<int>($"SELECT 1 AS [Value] FROM INFORMATION_SCHEMA.TABLES WHERE TABLE_NAME = 'Accounts'").AnyAsync(cancellationToken));

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
