using System.Globalization;
using OMTS.Application.Common.Interfaces.Persistence;
using OMTS.Application.Common.Messages;
using OMTS.Application.Common.Results;
using OMTS.Domain.Entities.Farming;

namespace OMTS.Application.Features.Cultivation.Progress;

/// <summary>The sessions, lots and manual changes of a cycle, read once for the percentage checks of one request.</summary>
public sealed record LanePercentContext(IReadOnlyList<LaneSeeding> Seedings, IReadOnlyList<LaneHarvest> Harvests, IReadOnlyList<LaneProgressStep> Steps);

/// <summary>
/// The percentage of each lane a seeding session or a harvest lot takes (owner decision of 12/11/2026): the form may send <c>lanePercents</c> (lane number to percent, above 0 and at most 100, two decimals) and the shares of a
/// lane add up to at most 100 in the cycle. Refused with MSG-GEN-01 on <c>LanePercent</c> (400), naming the lane and, when the share is what is too big, the percent left (<c>remainingPercent</c>). A lane the request does not
/// change keeps its share and is not judged again, so a session saved before the percentages can still be corrected.
/// </summary>
public sealed class LanePercentGuard(ISeedingRecordRepository seedings, IHarvestLotRepository harvests, ILaneProgressRepository progress)
{
    public const string Field = "LanePercent";

    public async Task<LanePercentContext> ReadAsync(Guid cycleId, CancellationToken cancellationToken) =>
        new(await seedings.LaneSeedingsAsync(cycleId, cancellationToken), await harvests.LaneHarvestsAsync(cycleId, cancellationToken), await progress.StepsAsync(cycleId, cancellationToken));

    /// <summary>
    /// The share each lane of a seeding session takes: the one sent, else the one the session already has, else 100. Keys are lane ids.
    /// </summary>
    /// <param name="existing">The shares the session being corrected already has; empty for a new session.</param>
    /// <param name="exceptRecord">The session being corrected, whose own share is not counted against itself.</param>
    public Result<IReadOnlyDictionary<Guid, decimal>> ForSeeding(
        LanePercentContext context, IReadOnlyList<Lane> lanes, IReadOnlyDictionary<int, decimal>? entered, bool unreadable,
        IReadOnlyDictionary<Guid, decimal> existing, Guid? exceptRecord)
    {
        if (ReadEntries(lanes, entered, unreadable) is { } invalid)
        {
            return invalid;
        }

        var shares = new Dictionary<Guid, decimal>();
        foreach (var lane in lanes)
        {
            var has = existing.TryGetValue(lane.Id, out var current);
            var share = entered is not null && entered.TryGetValue(lane.LaneNumber, out var sent) ? sent : has ? current : LanePercentRules.Whole;
            if (!has || share != current)
            {
                var others = LanePercentRules.Of(lane.Id, DateOnly.MaxValue, context.Seedings, context.Harvests, context.Steps, exceptRecord).Seeded;
                if (others + share > LanePercentRules.Whole)
                {
                    return TooBig(lane.LaneNumber, LanePercentRules.Remaining(others), "thả thêm");
                }
            }

            shares[lane.Id] = share;
        }

        return shares;
    }

    /// <summary>
    /// The share each lane of a harvest lot takes: the one sent, else the one the lot already has; a lane the lot finishes with no share takes what is left (the lane's total becomes 100), a lane not finished
    /// with none has no share (null: nobody entered it). Keys are lane ids.
    /// </summary>
    public Result<IReadOnlyDictionary<Guid, decimal?>> ForHarvest(
        LanePercentContext context, IReadOnlyList<Lane> lanes, IReadOnlySet<Guid> finished, IReadOnlyDictionary<int, decimal>? entered, bool unreadable,
        IReadOnlyDictionary<Guid, decimal?> existing, Guid? exceptLot)
    {
        if (ReadEntries(lanes, entered, unreadable) is { } invalid)
        {
            return invalid;
        }

        var shares = new Dictionary<Guid, decimal?>();
        foreach (var lane in lanes)
        {
            var has = existing.TryGetValue(lane.Id, out var current);
            var sent = entered is not null && entered.TryGetValue(lane.LaneNumber, out var value) ? value : (decimal?)null;
            var others = LanePercentRules.Of(lane.Id, DateOnly.MaxValue, context.Seedings, context.Harvests, context.Steps, exceptLot).Harvested;
            if (sent is { } explicitShare && (!has || explicitShare != current) && (others ?? 0m) + explicitShare > LanePercentRules.Whole)
            {
                return TooBig(lane.LaneNumber, LanePercentRules.Remaining(others), "thu thêm");
            }

            var share = sent ?? (has ? current : null);
            if (share is null && finished.Contains(lane.Id) && LanePercentRules.Remaining(others) is var left and > 0m)
            {
                share = left;
            }

            shares[lane.Id] = share;
        }

        return shares;
    }

    private static AppError? ReadEntries(IReadOnlyList<Lane> lanes, IReadOnlyDictionary<int, decimal>? entered, bool unreadable)
    {
        if (unreadable)
        {
            return Invalid(null, "lanePercents phải là một đối tượng JSON dạng {\"1\":60}.");
        }

        foreach (var (number, percent) in entered ?? new Dictionary<int, decimal>())
        {
            if (lanes.All(l => l.LaneNumber != number))
            {
                return Invalid(number, $"Làn {number} không nằm trong các làn của bản ghi.");
            }

            if (percent <= 0m || percent > LanePercentRules.Whole || decimal.Round(percent, 2) != percent)
            {
                return Invalid(number, $"Phần trăm của làn {number} phải lớn hơn 0 và tối đa 100, tối đa 2 chữ số thập phân.");
            }
        }

        return null;
    }

    private static AppError TooBig(int laneNumber, decimal remaining, string verb) =>
        Invalid(laneNumber, $"Làn {laneNumber} còn {remaining.ToString("0.##", CultureInfo.InvariantCulture)}% có thể {verb}.", remaining);

    private static AppError Invalid(int? laneNumber, string hint, decimal? remaining = null)
    {
        var details = new Dictionary<string, object?> { ["field"] = Field, ["hint"] = hint };
        if (laneNumber is { } number)
        {
            details["laneNumber"] = number;
        }

        if (remaining is { } left)
        {
            details["remainingPercent"] = left;
        }

        return new AppError(ErrorKind.Validation, UserMessages.Invalid("Lane percent"), details);
    }
}
