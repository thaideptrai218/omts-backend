using OMTS.Application.Common.Interfaces.Identity;
using OMTS.Application.Features.Audit;
using OMTS.Application.Features.Audit.Models;
using OMTS.Domain.Entities.Identity;

namespace OMTS.Infrastructure.Bootstrap.Demo;

/// <summary>
/// A demo database lives for days, but the bootstrap Owner's temporary password expires after 24 hours. While
/// the Owner has never logged in (still must change the password), this gives them the demo password, a
/// verified phone and the normal login branch. An Owner who already changed the password is left alone.
/// Recorded as a system action (no actor), like the bootstrap itself; the password is never logged.
/// </summary>
internal sealed class DemoOwnerStep : IDemoSeedStep
{
    public string Name => "owner login";

    public async Task<int> RunAsync(DemoSeedContext context, CancellationToken cancellationToken)
    {
        var owner = await context.Db.Accounts.FindAsync([context.OwnerId], cancellationToken) ?? throw new InvalidOperationException("Owner not found.");
        if (!owner.MustChangePassword || await context.DemoLoginAlreadySetAsync(owner.Id, cancellationToken))
        {
            return 0;
        }

        owner.PasswordHash = context.Get<IPasswordHasher>().Hash(DemoSeedContext.Password);
        owner.MustChangePassword = false;
        owner.TemporaryPasswordIssuedAt = null;
        owner.PhoneVerifiedAt ??= context.Now;
        context.Get<AuditLogService>().Record(new AuditEntry(DemoSeedContext.DemoLoginSetAction, nameof(Account))
        {
            RecordId = owner.Id,
            RecordCode = owner.Username,
            After = new { owner.Username, owner.MustChangePassword, PhoneVerified = true },
        });
        await context.Db.SaveChangesAsync(cancellationToken);
        return 1;
    }
}
