namespace OMTS.Infrastructure.Identity.Revocation;

/// <summary>
/// "Revocation" configuration section. The connection string is a secret and comes from <c>ConnectionStrings:Redis</c>
/// (user-secrets / environment variables), never from appsettings.json.
/// </summary>
public sealed class SessionRevocationOptions
{
    public const string SectionName = "Revocation";
    public const string RedisConnectionStringName = "Redis";

    /// <summary>The sweeper refreshes the "complete" marker every interval; it must expire well after that.</summary>
    public static readonly TimeSpan ReadyMarkerLifetime = TimeSpan.FromSeconds(60);

    /// <summary>Deny entries live this much longer than the access tokens issued before the revocation (30 s of accepted clock skew included).</summary>
    public static readonly TimeSpan DenyMargin = TimeSpan.FromMinutes(1);

    public static readonly TimeSpan MaximumSweepInterval = TimeSpan.FromSeconds(30);

    public RevocationMode Mode { get; set; } = RevocationMode.Auto;

    /// <summary>Set from <c>ConnectionStrings:Redis</c>, not from the section.</summary>
    public string? RedisConnectionString { get; set; }

    /// <summary>Every Redis command (read or write) is abandoned after this long.</summary>
    public TimeSpan CommandTimeout { get; set; } = TimeSpan.FromMilliseconds(250);

    /// <summary>How often each instance copies recent revocations from the database to Redis.</summary>
    public TimeSpan SweepInterval { get; set; } = TimeSpan.FromSeconds(10);

    public bool UseRedis => Mode switch
    {
        RevocationMode.Redis => true,
        RevocationMode.Sql => false,
        _ => !string.IsNullOrWhiteSpace(RedisConnectionString),
    };

    public bool IsValid(out string error)
    {
        if (Mode == RevocationMode.Redis && string.IsNullOrWhiteSpace(RedisConnectionString))
        {
            error = $"{SectionName}:Mode is Redis but 'ConnectionStrings:{RedisConnectionStringName}' is not set.";
            return false;
        }

        if (CommandTimeout <= TimeSpan.Zero || CommandTimeout > TimeSpan.FromSeconds(5))
        {
            error = $"{SectionName}:CommandTimeout must be above zero and at most 00:00:05.";
            return false;
        }

        if (SweepInterval < TimeSpan.FromSeconds(1) || SweepInterval > MaximumSweepInterval)
        {
            error = $"{SectionName}:SweepInterval must be from 00:00:01 to 00:00:30.";
            return false;
        }

        error = string.Empty;
        return true;
    }
}
