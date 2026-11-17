namespace OMTS.Application.Features.Cultivation.Progress;

/// <summary>What a lane holds in a cycle: the share hung (0 to 100) and the share harvested (0 to 100, or null when nobody entered it).</summary>
public sealed record LanePercents(decimal Seeded, decimal? Harvested);

/// <summary>One manual change of the progress of a lane (<c>LaneProgressChange</c>): the totals before and after, with who, when and why.</summary>
public sealed record LaneProgressStep(
    Guid CycleId,
    Guid LaneId,
    DateTimeOffset At,
    DateOnly On,
    Guid ByAccountId,
    string Reason,
    decimal OldSeeded,
    decimal? OldHarvested,
    decimal Seeded,
    decimal? Harvested);

/// <summary>
/// The percentages of a lane in a cycle as a pure function (owner decision of 12/11/2026). Sessions and lots add up, at most 100; a manual change (<see cref="LaneProgressStep"/>) is a correction that
/// wins over every session and lot recorded before it, while those recorded after it add to its totals. A lot that finishes the lane brings the harvest to 100; a lane in lots whose share nobody entered
/// has an unknown harvest (null), as every lane harvested before the percentages. The state of the lane follows from the two totals (<see cref="LaneStateRules"/>).
/// </summary>
public static class LanePercentRules
{
    public const decimal Whole = 100m;

    /// <summary>
    /// The totals of one lane on a date from the sessions and lots of its cycle dated up to then.
    /// </summary>
    /// <param name="exceptRecord">A session or lot left out: the one being corrected, whose own share is not counted against itself.</param>
    public static LanePercents Of(
        Guid laneId,
        DateOnly date,
        IReadOnlyCollection<LaneSeeding> seedings,
        IReadOnlyCollection<LaneHarvest> harvests,
        IReadOnlyCollection<LaneProgressStep> steps,
        Guid? exceptRecord = null)
    {
        var step = steps.Where(s => s.LaneId == laneId && s.On <= date).MaxBy(s => s.At);
        bool After(DateTimeOffset at) => step is null || at > step.At;

        var seeded = seedings.Where(s => s.LaneId == laneId && s.Date <= date && s.RecordId != exceptRecord && After(s.RecordedAt)).Sum(s => s.Percent);
        var lots = harvests.Where(h => h.LaneId == laneId && h.Date <= date && h.RecordId != exceptRecord && After(h.RecordedAt)).ToList();

        decimal? harvested;
        if (lots.Any(l => l.IsFinished))
        {
            harvested = Whole;
        }
        else if ((step is not null && step.Harvested is null) || lots.Any(l => l.Percent is null))
        {
            harvested = null;
        }
        else
        {
            harvested = Math.Min(Whole, (step?.Harvested ?? 0m) + lots.Sum(l => l.Percent!.Value));
        }

        return new LanePercents(Math.Min(Whole, (step?.Seeded ?? 0m) + seeded), harvested);
    }

    /// <summary>What can still be added to a total: 100 less what the lane holds, never below 0 (an unknown harvest counts as 0 here).</summary>
    public static decimal Remaining(decimal? total) => Math.Max(0m, Whole - (total ?? 0m));

    /// <summary>
    /// The state of the lane from its totals (BR-FARM-03 as the percentages see it): Harvested at 100 harvested; Being harvested when some share is harvested, or the share is unknown and the lane is in a lot;
    /// Growing while a share is hung; else Not seeded.
    /// </summary>
    public static LaneState StateOf(LanePercents percents, bool inALot) =>
        percents.Harvested == Whole ? LaneState.Harvested
        : (percents.Harvested is null ? inALot : percents.Harvested > 0m) ? LaneState.BeingHarvested
        : percents.Seeded > 0m ? LaneState.Growing
        : LaneState.NotSeeded;
}
