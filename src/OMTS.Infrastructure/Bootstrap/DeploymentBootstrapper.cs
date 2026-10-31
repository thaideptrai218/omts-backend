using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using OMTS.Application.Common.Interfaces.Identity;
using OMTS.Application.Common.Validation;
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
/// creates the rows of the two fixed roles (BR-FARM-07) and, when no cooperative exists, the cooperative (full name only) and its first
/// Cooperative Owner with a temporary password that expires after 24 hours. Idempotent.
/// </summary>
public sealed class DeploymentBootstrapper(
    OmtsDbContext db,
    IConfiguration configuration,
    IHostEnvironment environment,
    IPasswordHasher passwordHasher,
    AuditLogService audit,
    TimeProvider timeProvider,
    IServiceProvider services)
{
    public const int Success = 0;
    public const int Failure = 1;

    public async Task<BootstrapOutcome> RunAsync(bool seedDemoData, CancellationToken cancellationToken)
    {
        var messages = new List<string>();
        var settings = BootstrapSettings.From(configuration);

        // The demo data gives well-known passwords to the Owner and staff accounts: Development only, with no
        // switch that opens it anywhere else.
        if (seedDemoData && !environment.IsDevelopment())
        {
            return new BootstrapOutcome(Failure, ["Demo data is refused outside Development."]);
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
                    ? "Demo mode: the Owner gets the fixed demo password instead."
                    : "The temporary password expires in 24 hours and must be changed at first login."));
        }
        else
        {
            messages.Add("Cooperative already initialized; nothing changed.");
        }

        await db.SaveChangesAsync(cancellationToken);

        if (seedDemoData)
        {
            // After the save above: the demo steps act as the Owner, which must exist in the database. The
            // lines never contain a password.
            messages.AddRange(await DemoDataSeeder.SeedAsync(services, settings.OwnerUsername, cooperative.Id, cancellationToken));
        }

        return new BootstrapOutcome(Success, messages);
    }

    private async Task<int> MissingStatusCatalogRowsAsync(CancellationToken cancellationToken)
    {
        var statusIds = await db.Statuses.Select(s => s.Id).ToListAsync(cancellationToken);
        var transitionIds = await db.StatusTransitions.Select(t => t.Id).ToListAsync(cancellationToken);
        return StatusCatalogSeed.Statuses.Count(s => !statusIds.Contains(s.Id))
            + StatusCatalogSeed.Transitions.Count(t => !transitionIds.Contains(t.Id));
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
        var cooperative = new Cooperative { FullName = settings.CooperativeFullName!.Trim() };
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
            After = new { cooperative.FullName, Status = "DRAFT" },
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
