using OMTS.Application.Common;
using OMTS.Application.Common.Interfaces.Persistence;
using OMTS.Application.Common.Results;
using OMTS.Domain.Enums;

namespace OMTS.Application.Features.Farming.Rafts;

/// <summary>
/// FARM-04 lane segment condition view: the raft map of a date (or a cycle), built from the cycle's
/// recorded lane segments over the layout version valid on that date. Open to staff of the raft's zone.
/// </summary>
public sealed class RaftConditionService(IRaftRepository rafts, FarmingLookupAccess lookupAccess, TimeProvider timeProvider)
{
    public async Task<Result<RaftConditionView>> GetAsync(Guid raftId, DateOnly? date, Guid? cycleId, CancellationToken cancellationToken)
    {
        var raft = await rafts.GetForEditAsync(raftId, cancellationToken);
        if (raft is null)
        {
            return AppError.NotFound;
        }

        if (!await lookupAccess.CanSeeZoneAsync(raft.ZoneId, cancellationToken))
        {
            return AppError.Forbidden;
        }

        var day = date ?? timeProvider.Today();
        var cycle = await rafts.FindCycleAsync(raft.Id, cycleId, day, cancellationToken);
        if (cycleId is not null && cycle is null)
        {
            return AppError.Invalid("Cycle");
        }

        var version = RaftLayout.VersionOn(raft.LayoutVersions, day);
        var layout = version is null
            ? raft.Lanes.Select(l => new LayoutLane(l.LaneNumber, l.Length ?? 0, string.Empty, string.Empty)).ToList()
            : RaftLayout.LanesOf(version);
        var laneIds = raft.Lanes.ToDictionary(l => l.LaneNumber, l => l.Id);
        var segments = cycle is null ? [] : await rafts.SegmentsAsync(cycle.Id, day, cancellationToken);

        var lanes = layout.OrderBy(l => l.LaneNumber).Select(lane =>
        {
            var ofLane = laneIds.TryGetValue(lane.LaneNumber, out var laneId) ? segments.Where(s => s.LaneId == laneId).ToList() : [];
            var byId = ofLane.ToDictionary(s => s.Id);
            var portions = LaneConditionRules.Portions(lane.Length, ofLane.Select(s => new SegmentFact(s.Id, s.Type, s.From, s.To, s.Date)).ToList());
            return new LaneConditionView(
                lane.LaneNumber,
                lane.Length,
                LaneConditionRules.Summarize(portions),
                portions.Select(p => new PortionView(p.From, p.To, p.Condition, p.SegmentIds.Select(id => Source(byId[id])).ToList())).ToList(),
                ofLane.Where(s => s.Type == LaneSegmentType.PlannedHarvest).Select(Source).ToList());
        }).ToList();

        return new RaftConditionView(raft.Id, raft.Code, day, cycle?.Id, version?.VersionNumber ?? 0, lanes);
    }

    private static SegmentSourceView Source(LaneSegmentDetail s) => new(
        s.Id, s.Type, s.From, s.To, s.Date, s.RecordedBy, s.ConfirmedBy, s.SeedingEventId, s.SeedLotCode, s.HarvestEventId, s.LotId, s.WriteOffReason);
}
