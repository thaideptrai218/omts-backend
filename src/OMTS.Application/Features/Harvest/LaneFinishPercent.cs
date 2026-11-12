using OMTS.Application.Common.Interfaces.Persistence;
using OMTS.Application.Features.Cultivation.Progress;
using OMTS.Domain.Entities.Cultivation;
using OMTS.Domain.Entities.Harvest;

namespace OMTS.Application.Features.Harvest;

/// <summary>
/// What "finish harvest" does to the percentages of a lane (owner decision of 12/11/2026): the lane's harvested total becomes 100. The finish mark on the lane row of the lot already does that
/// (<see cref="LanePercentRules"/>); the row also takes the share that was left when it had none, so the shares of the lots add up. A lane the Zone Manager or Owner corrected by hand needs one more row, as a
/// manual change wins over the lots recorded before it: the finish is then recorded as a change too.
/// </summary>
internal static class LaneFinishPercent
{
    public const string FinishReason = "Hoàn tất thu hoạch";

    /// <param name="row">The lane row of the lot that carries the finish mark (tracked).</param>
    /// <param name="inLots">Every lane row of the lane in the cycle, the lot of <paramref name="row"/> included.</param>
    /// <param name="current">The totals of the lane before the finish; <paramref name="hasManualChange"/> when it was changed by hand in this cycle.</param>
    public static void Apply(
        HarvestLotLane row, IReadOnlyList<HarvestLotLane> inLots, LanePercents current, bool hasManualChange, ILaneProgressRepository progress, Guid cycleId, Guid actor, DateTimeOffset now, DateOnly today)
    {
        var left = LanePercentRules.Remaining(inLots.Where(l => l.LotId != row.LotId).Sum(l => l.HarvestedPercent ?? 0m));
        row.HarvestedPercent ??= left > 0m ? left : null;
        if (!hasManualChange)
        {
            return;
        }

        RecordWhole(row.LaneId, current, progress, cycleId, actor, now, today);
    }

    /// <summary>
    /// Finishing a lane that has NO harvest lot, only a share harvested entered by hand (QA of 13/11/2026: such a lane is Being harvested, and the finish answered 409 for want of a lot): the lane is
    /// completed by one more manual change, harvested 100 and seeded as it is, recorded with the reason <see cref="FinishReason"/>. No lot is created or edited.
    /// </summary>
    public static void RecordWhole(Guid laneId, LanePercents current, ILaneProgressRepository progress, Guid cycleId, Guid actor, DateTimeOffset now, DateOnly today) =>
        progress.Add(new LaneProgressChange
        {
            CycleId = cycleId,
            LaneId = laneId,
            OccurredAt = now,
            OccurredOn = today,
            ChangedByAccountId = actor,
            Reason = FinishReason,
            OldSeededPercent = current.Seeded,
            OldHarvestedPercent = current.Harvested,
            SeededPercent = current.Seeded,
            HarvestedPercent = LanePercentRules.Whole,
        });
}
