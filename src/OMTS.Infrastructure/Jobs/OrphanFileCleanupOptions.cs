using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using OMTS.Application.Features.Files;

namespace OMTS.Infrastructure.Jobs;

/// <summary>"Jobs:OrphanFileCleanup" section.</summary>
public sealed class OrphanFileCleanupOptions
{
    public const string SectionName = "Jobs:OrphanFileCleanup";

    /// <summary>The shortest grace period accepted: an upload and its save never take that long.</summary>
    public static readonly TimeSpan MinimumGracePeriod = TimeSpan.FromHours(1);

    /// <summary>False stops the daily run; <see cref="OrphanFileCleanupJob.RunOnceAsync"/> still works when called.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>Daily run time in the business time zone (UTC+7), outside working hours.</summary>
    public TimeOnly RunAt { get; set; } = new(3, 0);

    /// <summary>A file written less than this long ago is kept, so a file uploaded right now is never deleted.</summary>
    public TimeSpan GracePeriod { get; set; } = TimeSpan.FromHours(24);
}
