using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using OMTS.Application.Common.Interfaces.Persistence;

namespace OMTS.Infrastructure.Jobs;

/// <summary>"Jobs:LoginSessionCleanup" section.</summary>
public sealed class LoginSessionCleanupOptions : IDailyJobOptions
{
    public const string SectionName = "Jobs:LoginSessionCleanup";

    /// <summary>
    /// The shortest retention accepted. An ended session is kept well past the lifetime of the revocation store's entries (about
    /// 16 minutes) and of any access token, so deleting it can never make a revoked session look valid.
    /// </summary>
    public static readonly TimeSpan MinimumRetention = TimeSpan.FromDays(1);

    /// <summary>False stops the daily run; <see cref="LoginSessionCleanupJob.RunOnceAsync"/> still works when called.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>Daily run time in the business time zone (UTC+7), outside working hours.</summary>
    public TimeOnly RunAt { get; set; } = new(3, 30);

    /// <summary>Off by default: a missed day only delays the clean-up, and a second run finds nothing more to delete.</summary>
    public bool CatchUpOnStart { get; set; }

    /// <summary>How long a session is kept after it expired or was revoked: the device and address history the owner can still see.</summary>
    public TimeSpan Retention { get; set; } = TimeSpan.FromDays(30);
}

/// <summary>
/// Deletes login sessions that expired or were revoked more than <see cref="LoginSessionCleanupOptions.Retention"/> ago. The
/// device id hash, address and browser of a session are personal data; they do not outlive that period.
/// </summary>
public sealed class LoginSessionCleanupJob(IServiceScopeFactory scopes, IOptions<LoginSessionCleanupOptions> options)
{
    /// <summary>Number of sessions deleted; sessions that ended within the retention period before <paramref name="now"/> are kept.</summary>
    public async Task<int> RunOnceAsync(DateTimeOffset now, CancellationToken cancellationToken = default)
    {
        using var scope = scopes.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<ILoginSessionRepository>()
            .DeleteEndedBeforeAsync(now - options.Value.Retention, cancellationToken);
    }
}

/// <summary>Runs <see cref="LoginSessionCleanupJob"/> once a day at <see cref="LoginSessionCleanupOptions.RunAt"/> (see <see cref="DailyJobHostedService"/>).</summary>
internal sealed class LoginSessionCleanupHostedService(
    LoginSessionCleanupJob job,
    IOptions<LoginSessionCleanupOptions> options,
    TimeProvider timeProvider,
    ILogger<LoginSessionCleanupHostedService> logger) : DailyJobHostedService(options.Value, timeProvider, logger)
{
    protected override string JobName => "Login session clean-up";

    protected override async Task RunOnceAsync(DateOnly today, CancellationToken cancellationToken)
    {
        var deleted = await job.RunOnceAsync(Clock.GetUtcNow(), cancellationToken);
        Logger.LogInformation("Login session clean-up deleted {Count} sessions.", deleted);
    }
}
