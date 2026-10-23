using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using OMTS.Application.Common.Results;
using OMTS.Infrastructure.Persistence;

namespace OMTS.Infrastructure.Bootstrap.Demo;

/// <summary>
/// What every demo step needs: the scope's services (the same ones the API uses, acting as the Owner),
/// the database, and "today" in the business time zone (UTC+7) that all demo dates are relative to.
/// </summary>
internal sealed class DemoSeedContext(IServiceProvider services, OmtsDbContext db, Guid cooperativeId, Guid ownerId, DateTimeOffset now)
{
    public const string Password = "Demo@1234";

    /// <summary>Audit action written when an account gets its demo login; at most once per account.</summary>
    public const string DemoLoginSetAction = "Account.DemoCredentialsSet";

    public OmtsDbContext Db { get; } = db;
    public Guid CooperativeId { get; } = cooperativeId;
    public Guid OwnerId { get; } = ownerId;
    public DateTimeOffset Now { get; } = now;

    /// <summary>Today in UTC+7, the same day the application services use.</summary>
    public DateOnly Today { get; } = DateOnly.FromDateTime(now.ToOffset(TimeSpan.FromHours(7)).DateTime);

    public T Get<T>() where T : notnull => services.GetRequiredService<T>();

    /// <summary>
    /// Whether an earlier run already gave this account its demo login. From then on, whatever happens to the
    /// account (a password reset by the Owner, a changed password, a verified phone) is real use that a rerun
    /// must not undo.
    /// </summary>
    public Task<bool> DemoLoginAlreadySetAsync(Guid accountId, CancellationToken cancellationToken) =>
        Db.AuditEvents.AnyAsync(e => e.Action == DemoLoginSetAction && e.RecordId == accountId, cancellationToken);

    /// <summary>The value of a successful service call; a refusal stops the seed with the service's own message.</summary>
    public static T Ensure<T>(Result<T> result, string what) =>
        result.IsSuccess
            ? result.Value!
            : throw new InvalidOperationException($"Demo data: {what} was refused ({result.Error!.Message}).");
}
