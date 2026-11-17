using OMTS.Application.Common.Interfaces.Persistence;

namespace OMTS.Application.Features.Cultivation.Progress;

/// <summary>One manual change of the progress of a lane as the client reads it: the totals before and after, who, when and why.</summary>
public sealed record LaneProgressChangeView(
    DateTimeOffset At, string? ByName, string? Reason, decimal OldSeededPercent, decimal SeededPercent, decimal? OldHarvestedPercent, decimal? HarvestedPercent);

/// <summary>
/// The percentages of a lane in a cycle as the lane views carry them (owner decision of 12/11/2026): <c>seededPercent</c> and <c>harvestedPercent</c> (null: nobody entered it), and the manual changes
/// (<c>lastModifiedByName</c>, <c>lastModifiedAt</c>, <c>progressHistory</c>, the newest first; empty when nobody changed the lane by hand).
/// </summary>
public sealed record LaneProgressInfo(
    decimal SeededPercent, decimal? HarvestedPercent, string? LastModifiedByName, DateTimeOffset? LastModifiedAt, IReadOnlyList<LaneProgressChangeView> ProgressHistory);

/// <summary>Builds the percentages of the lanes of a day for the lane views, with the display names of the people who changed them.</summary>
public sealed class LaneProgressViews(IAccountRepository accounts)
{
    public async Task<IReadOnlyDictionary<Guid, LaneProgressInfo>> InfoAsync(LaneDay day, CancellationToken cancellationToken)
    {
        var steps = day.Steps ?? [];
        var names = steps.Count == 0
            ? new Dictionary<Guid, string>()
            : (await accounts.GetDisplayNamesAsync(steps.Select(s => s.ByAccountId).Distinct().ToList(), cancellationToken)).ToDictionary(p => p.Key, p => p.Value);
        return day.States.Keys.ToDictionary(id => id, id => Info(id, day.Percents?.GetValueOrDefault(id) ?? new LanePercents(0m, 0m), steps, names));
    }

    /// <summary>The percentages and history of one lane.</summary>
    public static LaneProgressInfo Info(Guid laneId, LanePercents percents, IReadOnlyList<LaneProgressStep> steps, IReadOnlyDictionary<Guid, string> names)
    {
        var history = steps.Where(s => s.LaneId == laneId).OrderByDescending(s => s.At)
            .Select(s => new LaneProgressChangeView(s.At, names.GetValueOrDefault(s.ByAccountId), s.Reason, s.OldSeeded, s.Seeded, s.OldHarvested, s.Harvested))
            .ToList();
        return new LaneProgressInfo(percents.Seeded, percents.Harvested, history.FirstOrDefault()?.ByName, history.FirstOrDefault()?.At, history);
    }
}
