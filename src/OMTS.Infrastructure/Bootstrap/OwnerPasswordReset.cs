using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using OMTS.Application.Common.Interfaces.Identity;
using OMTS.Application.Common.Interfaces.Persistence;
using OMTS.Application.Common.Models;
using OMTS.Application.Common.Validation;
using OMTS.Application.Features.Audit;
using OMTS.Application.Features.Audit.Models;
using OMTS.Domain.Entities.Identity;
using OMTS.Domain.Enums;
using OMTS.Infrastructure.Persistence;

namespace OMTS.Infrastructure.Bootstrap;

/// <summary>
/// <c>dotnet OMTS.Api.dll seed --reset-owner-password</c>: recovery for a Cooperative Owner who cannot log in,
/// typically after missing the 24-hour window of the bootstrap's temporary password. The new temporary
/// password comes from <see cref="BootstrapSettings.OwnerTemporaryPasswordKey"/> (configuration, never a
/// command-line argument, so it never lands in shell history). Same effect as an Owner's reset of a staff
/// password, namely a temporary one that expires after 24 hours and must be changed at first login; every
/// session ends and the reset is audited.
/// </summary>
public sealed class OwnerPasswordReset(
    OmtsDbContext db,
    IConfiguration configuration,
    IPasswordHasher passwordHasher,
    ILoginSessionRepository sessions,
    AuditLogService audit,
    TimeProvider timeProvider)
{
    public async Task<BootstrapOutcome> RunAsync(CancellationToken cancellationToken)
    {
        var settings = BootstrapSettings.From(configuration);
        if (string.IsNullOrWhiteSpace(settings.OwnerTemporaryPassword))
        {
            return Fail($"Missing bootstrap secrets: {BootstrapSettings.OwnerTemporaryPasswordKey}.");
        }

        if (!PasswordPolicy.IsValid(settings.OwnerTemporaryPassword))
        {
            return Fail($"{BootstrapSettings.OwnerTemporaryPasswordKey} must have at least 8 characters with a letter and a digit.");
        }

        var owners = await db.Accounts.Where(a => a.SubjectType == SubjectType.CooperativeOwner).ToListAsync(cancellationToken);
        var owner = string.IsNullOrWhiteSpace(settings.OwnerUsername)
            ? owners.Count == 1 ? owners[0] : null
            : owners.SingleOrDefault(a => a.Username == settings.OwnerUsername.Trim());
        if (owner is null)
        {
            return Fail(owners.Count switch
            {
                0 => "No Cooperative Owner exists; run seed --bootstrap first.",
                _ when !string.IsNullOrWhiteSpace(settings.OwnerUsername) => $"No Cooperative Owner has the username set in {BootstrapSettings.OwnerUsernameKey}.",
                _ => $"Several Cooperative Owners exist; set {BootstrapSettings.OwnerUsernameKey} to choose one.",
            });
        }

        // The key is the bootstrap's own: a value left in place would hand out the expired password again.
        if (passwordHasher.Verify(owner.PasswordHash, settings.OwnerTemporaryPassword) != PasswordCheck.Failed)
        {
            return Fail($"{BootstrapSettings.OwnerTemporaryPasswordKey} is the Owner's current password; set a new value first.");
        }

        var now = timeProvider.GetUtcNow();
        owner.PasswordHash = passwordHasher.Hash(settings.OwnerTemporaryPassword);
        owner.MustChangePassword = true;
        owner.TemporaryPasswordIssuedAt = now;
        owner.FailedLoginCount = 0;
        owner.LockoutEndAt = null;
        await sessions.RevokeAllAsync(owner.Id, now, "Temporary password issued", cancellationToken);

        // A system action like the bootstrap: the actor stays empty.
        audit.Record(new AuditEntry("Account.PasswordReset", nameof(Account))
        {
            RecordId = owner.Id,
            RecordCode = owner.Username,
            After = new { owner.MustChangePassword, owner.TemporaryPasswordIssuedAt, SessionsRevoked = true, Source = "seed --reset-owner-password" },
        });
        await db.SaveChangesAsync(cancellationToken);

        return new BootstrapOutcome(DeploymentBootstrapper.Success,
        [
            $"Temporary password set for Cooperative Owner '{owner.Username}'. It expires in 24 hours and must be changed at first login; every session was ended.",
        ]);
    }

    private static BootstrapOutcome Fail(string message) => new(DeploymentBootstrapper.Failure, [message]);
}
