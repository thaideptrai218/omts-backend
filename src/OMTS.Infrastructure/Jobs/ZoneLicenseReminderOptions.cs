using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using OMTS.Application.Features.Farming.Licenses;

namespace OMTS.Infrastructure.Jobs;

/// <summary>"Jobs:ZoneLicenseReminder" section.</summary>
public sealed class ZoneLicenseReminderOptions : IDailyJobOptions
{
    public const string SectionName = "Jobs:ZoneLicenseReminder";

    /// <summary>Daily run time in the business time zone (UTC+7).</summary>
    public TimeOnly RunAt { get; set; } = new(7, 0);

    /// <summary>
    /// A host that starts after today's run time (deploy, restart) runs once at start, so that day is not
    /// skipped. Safe because a notice already sent is never sent again.
    /// </summary>
    public bool CatchUpOnStart { get; set; } = true;
}
