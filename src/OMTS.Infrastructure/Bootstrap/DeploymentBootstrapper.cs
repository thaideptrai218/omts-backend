using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using OMTS.Application.Common.Interfaces.Identity;
using OMTS.Application.Common.Validation;
using OMTS.Application.Features.AccessManagement;
using OMTS.Application.Features.Audit;
using OMTS.Application.Features.Audit.Models;
using OMTS.Domain.Constants;
using OMTS.Domain.Entities.Identity;
using OMTS.Domain.Enums;
using OMTS.Infrastructure.Bootstrap.Demo;
using OMTS.Infrastructure.Persistence;
using OMTS.Infrastructure.Persistence.Seed;

namespace OMTS.Infrastructure.Bootstrap;

/// <summary>Exit code and console lines of the bootstrap command; never contains a password.</summary>
public sealed record BootstrapOutcome(int ExitCode, IReadOnlyList<string> Messages);

/// <summary>
/// One-time deployment step (formerly UC72; no System Administrator since 22/10):
/// <c>dotnet OMTS.Api.dll seed --bootstrap [--demo]</c> applies migrations, checks the status catalog,
/// creates the rows of the two fixed roles (BR-FARM-07), matches the old customer and supplier addresses to the administrative units list (<see cref="AddressCodeBackfill"/>) and, when no cooperative exists, the cooperative (full name only) and its first
/// Cooperative Owner with a temporary password that expires after 24 hours. Idempotent.
/// </summary>
public sealed class DeploymentBootstrapper(
    OmtsDbContext db,
    IConfiguration configuration,
    IHostEnvironment environment,
    IPasswordHasher passwordHasher,
    AuditLogService audit,
    TimeProvider timeProvider,
    AddressCodeBackfill addressCodes,
    SeasonalAccountBackfill seasonalAccounts,
    IServiceProvider services)
{
    public const int Success = 0;
    public const int Failure = 1;

    public async Task<BootstrapOutcome> RunAsync(bool seedDemoData, CancellationToken cancellationToken)
    {
        var messages = new List<string>();
        var settings = BootstrapSettings.From(configuration);

        // The demo data gives one shared password to the Owner, staff and traders. Decided before anything is written.
        var demoPassword = string.Empty;
        if (seedDemoData)
        {
            var (password, refusal) = DemoSeedGate.Check(environment, configuration);
            if (refusal is not null)
            {
                return new BootstrapOutcome(Failure, [refusal]);
            }

            demoPassword = password!;
        }

        var (databaseRefusal, demoMarkNeeded) = await DemoSeedGate.CheckDatabaseAsync(environment, db, passwordHasher, seedDemoData, cancellationToken);
        if (databaseRefusal is not null)
        {
            return new BootstrapOutcome(Failure, [databaseRefusal]);
        }

        if (db.Database.IsRelational())
        {
            await db.Database.MigrateAsync(cancellationToken);
            messages.Add("Database migrations applied.");
        }
        else
        {
            await db.Database.EnsureCreatedAsync(cancellationToken);
        }

        var missingStatuses = await MissingStatusCatalogRowsAsync(cancellationToken);
        if (missingStatuses > 0)
        {
            return new BootstrapOutcome(Failure, [.. messages, $"Status catalog is incomplete: {missingStatuses} rows missing."]);
        }

        var cooperative = await db.Cooperatives.OrderBy(c => c.CreatedAt).FirstOrDefaultAsync(cancellationToken);
        if (cooperative is null)
        {
            var problems = Validate(settings);
            if (problems.Count > 0)
            {
                return new BootstrapOutcome(Failure, [.. messages, .. problems]);
            }
        }

        var roles = await DefaultRoleSeeder.SeedAsync(db, audit, cancellationToken);
        messages.Add(roles.Count == 0 ? "Default roles already exist." : $"Default roles created: {string.Join(", ", roles)}.");

        if (cooperative is null)
        {
            cooperative = CreateCooperativeAndOwner(settings);
            messages.Add($"Cooperative created with Cooperative Owner '{settings.OwnerUsername}'. " +
                (seedDemoData
                    ? "Demo mode: the Owner gets the demo password instead."
                    : "The first password must be changed at first login and does not expire."));

            // Saved with the cooperative and its Owner, before the first demo step: a database with these accounts and
            // without the mark cannot come from a crash of this run.
            if (demoMarkNeeded)
            {
                audit.Record(new AuditEntry(DemoSeedGate.DemoSeededAction, nameof(Cooperative)) { RecordId = cooperative.Id });
            }
        }
        else
        {
            messages.Add("Cooperative already initialized; nothing changed.");
        }

        await db.SaveChangesAsync(cancellationToken);

        // Accounts deleted before the "cách A" rule (26/11/2026) free their username, phone and email now. Idempotent.
        var released = await ReleaseDeletedAccountIdentifiersAsync(cancellationToken);
        if (released > 0)
        {
            messages.Add($"Deleted accounts: {released} had their username, phone and email freed.");
        }

        if (seedDemoData)
        {
            // After the save above: the demo steps act as the Owner, which must exist in the database. The
            // lines never contain a password. In Staging the Owner's login is set only by the run that wrote the mark.
            var ownerLoginMayBeSet = environment.IsDevelopment() || demoMarkNeeded;
            messages.AddRange(await DemoDataSeeder.SeedAsync(services, settings.OwnerUsername, cooperative.Id, demoPassword, ownerLoginMayBeSet, cancellationToken));
        }

        // Last, and never a reason to fail: it only needs the outside list of provinces and wards, which may be out of reach.
        // On a shared demo server every account has the demo secret and no account may hold a fixed, well-known password, so the seasonal accounts are not backfilled there.
        messages.Add(seedDemoData && !environment.IsDevelopment()
            ? "Seasonal accounts: skipped (demo data on a shared server: no account holds the fixed initial password)."
            : await seasonalAccounts.RunAsync(cancellationToken));
        messages.Add(await addressCodes.RunAsync(cancellationToken));

        return new BootstrapOutcome(Success, messages);
    }

    private async Task<int> ReleaseDeletedAccountIdentifiersAsync(CancellationToken cancellationToken)
    {
        var deleted = await db.Accounts.Where(a => a.StatusId == StatusIds.Account.Deleted && !a.Username.Contains(DeletedAccountIdentifiers.Marker)).ToListAsync(cancellationToken);
        foreach (var account in deleted)
        {
            DeletedAccountIdentifiers.Release(account);
        }

        if (deleted.Count > 0)
        {
            await db.SaveChangesAsync(cancellationToken);
        }

        return deleted.Count;
    }

    private async Task<int> MissingStatusCatalogRowsAsync(CancellationToken cancellationToken)
    {
        var statusIds = await db.Statuses.Select(s => s.Id).ToListAsync(cancellationToken);
        return StatusCatalogSeed.Statuses.Count(s => !statusIds.Contains(s.Id));
    }

    private static List<string> Validate(BootstrapSettings settings)
    {
        var missing = settings.MissingKeys();
        if (missing.Count > 0)
        {
            return [$"Missing bootstrap secrets: {string.Join(", ", missing)}."];
        }

        var problems = new List<string>();
        if (PhoneNumbers.Normalize(settings.OwnerPhoneNumber) is null)
        {
            problems.Add($"{BootstrapSettings.OwnerPhoneNumberKey} is not a valid phone number.");
        }

        if (!PasswordPolicy.IsValid(settings.OwnerTemporaryPassword))
        {
            problems.Add($"{BootstrapSettings.OwnerTemporaryPasswordKey} must have at least 8 characters with a letter and a digit.");
        }

        return problems;
    }

    private Cooperative CreateCooperativeAndOwner(BootstrapSettings settings)
    {
        var now = timeProvider.GetUtcNow();
        // A new cooperative may not issue more than it has on hand (decision of 11/11/2026); the Owner can allow a negative stock in the input settings. Existing cooperatives keep their value.
        var cooperative = new Cooperative { FullName = settings.CooperativeFullName!.Trim(), AllowNegativeInputQuantity = false };
        var owner = new Account
        {
            SubjectType = SubjectType.CooperativeOwner,
            DisplayName = settings.OwnerDisplayName!.Trim(),
            Username = settings.OwnerUsername!.Trim(),
            PhoneNumber = PhoneNumbers.Normalize(settings.OwnerPhoneNumber),
            Email = string.IsNullOrWhiteSpace(settings.OwnerEmail) ? null : settings.OwnerEmail.Trim(),
            PasswordHash = passwordHasher.Hash(settings.OwnerTemporaryPassword!),
            MustChangePassword = true,
            TemporaryPasswordIssuedAt = now,
            StatusId = StatusIds.Account.Active,
        };

        db.Cooperatives.Add(cooperative);
        db.Accounts.Add(owner);

        // System actions: the actor stays empty.
        audit.Record(new AuditEntry("Cooperative.Created", nameof(Cooperative))
        {
            RecordId = cooperative.Id,
            // The cooperative has no status (SRS 27/10).
            After = new { cooperative.FullName },
        });
        audit.Record(new AuditEntry("Account.Created", nameof(Account))
        {
            RecordId = owner.Id,
            RecordCode = owner.Username,
            After = new { owner.Username, owner.DisplayName, owner.PhoneNumber, owner.Email, owner.SubjectType, owner.MustChangePassword },
        });

        return cooperative;
    }
}
