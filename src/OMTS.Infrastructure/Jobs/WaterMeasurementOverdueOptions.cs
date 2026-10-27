namespace OMTS.Infrastructure.Jobs;

/// <summary>"Jobs:WaterMeasurementOverdue" section of the overdue water measurement reminder (formerly JOB-05).</summary>
public sealed class WaterMeasurementOverdueOptions : IDailyJobOptions
{
    public const string SectionName = "Jobs:WaterMeasurementOverdue";

    /// <summary>Daily run time in the business time zone (UTC+7).</summary>
    public TimeOnly RunAt { get; set; } = new(6, 0);

    /// <summary>
    /// A host that starts after today's run time (deploy, restart) runs once at start, so that day is not skipped.
    /// Safe because a zone gets MSG26 once per overdue period (marker <c>FarmingZone.WaterOverdueRemindedAt</c>).
    /// </summary>
    public bool CatchUpOnStart { get; set; } = true;
}
