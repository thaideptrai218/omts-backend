namespace OMTS.Application.Features.Harvest;

/// <summary>When the next GFR-05 daily close check of harvest lots runs (the run time is a setting of the job host).</summary>
public interface IHarvestAutoCloseSchedule
{
    /// <summary>The business date of the next check: today before today's run time, tomorrow after it.</summary>
    DateOnly NextCheckDate();
}
