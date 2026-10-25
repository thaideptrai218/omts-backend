namespace OMTS.Infrastructure.Jobs;

/// <summary>Schedule of a daily job (<see cref="DailyJobHostedService"/>), bound from its "Jobs:&lt;Name&gt;" section.</summary>
public interface IDailyJobOptions
{
    /// <summary>Daily run time in the business time zone (UTC+7).</summary>
    TimeOnly RunAt { get; }

    /// <summary>
    /// A host that starts after today's run time (deploy, restart) runs once at start, so that day is not skipped.
    /// Safe because each job sends a notice once per period and keeps a marker of it.
    /// </summary>
    bool CatchUpOnStart { get; }
}
