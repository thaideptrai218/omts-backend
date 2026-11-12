using OMTS.Application.Common;
using OMTS.Application.Common.Results;
using OMTS.Application.Features.Cultivation.Nursery;
using OMTS.Application.Features.Cultivation.Progress;
using OMTS.Application.Features.Cultivation.Seeding;
using OMTS.Application.Features.Farming;
using OMTS.Application.Features.Purchasing.Receipts;
using OMTS.Domain.Constants;
using OMTS.Domain.Entities.Cultivation;
using OMTS.Domain.Entities.Farming;
using OMTS.Domain.Entities.Harvest;

namespace OMTS.Application.Features.Traceability.Corrections;

/// <summary>
/// TRACE-03.0 step 5: the seeding rules of CYCLE-02 (BR-CYCLE-02, GBR-04) on the corrected session, in the session's own cycle and on its own date, as a Closed cycle has no
/// lanes today. The date is not before the start or the date the seed lot was received, and not after today or the close date; the quantity is above 0; one seed lot; a reason.
/// A lane the correction adds, or every lane when the date moves, must be free to seed on that date without the session itself: Not seeded, or Growing (re-hanging) while the
/// seeding was not yet marked complete; never Being harvested or Harvested. Lanes the session keeps stay allowed, also one Removed since the close (BR-FARM-04).
/// </summary>
public sealed class TraceSeedingReader(SeedingSessionReader sessions, TimeProvider timeProvider)
{
    /// <param name="ofCycle">Every session of the cycle, the corrected one among them.</param>
    /// <param name="lots">Every lot of the cycle with its lanes.</param>
    internal async Task<(SeedingFields? Fields, AppError? Error)> ReadAsync(
        SeedingRecord record, Raft raft, TraceSeedingCorrectionRequest request, IReadOnlyList<SeedingRecord> ofCycle, IReadOnlyList<HarvestLot> lots, CancellationToken cancellationToken)
    {
        var cycle = record.Cycle;
        var today = timeProvider.Today();
        var lastDay = cycle.CloseDate is { } closed && closed < today ? closed : today;
        var lot = await sessions.LotAsync(request.SeedLotId, record, cancellationToken);

        // Owner decision of 13/11/2026: a session from a nursery hangs all that is left of the share of its cycle; its quantity is not chosen here either.
        var wanted = request.Quantity;
        if (record.NurseryBatchId is not null)
        {
            var full = NurseryRules.FullSeeding(cycle.NurseryAllocatedQuantity ?? 0m, ofCycle.Where(s => s.Id != record.Id).Sum(s => s.Quantity), request.Quantity);
            if (full.Error is not null)
            {
                return (null, full.Error);
            }

            wanted = full.Quantity;
        }

        var check = new FieldCheck();
        var dateIsValid = request.SeedingDate is { } date && date >= cycle.StartDate && date <= lastDay && (lot is null || date >= lot.Receipt.ReceiptDate);
        check.Check(dateIsValid, SeedingValues.DateField);
        check.Check(lot is not null, SeedingValues.SeedLotField);
        check.Check(
            wanted is { } quantity && quantity > 0 && quantity <= PurchaseReceiptValidator.MaxQuantity && PurchaseReceiptValidator.HasAllowedDecimals(quantity),
            SeedingValues.QuantityField);
        var (lanes, laneHint) = Lanes(record, raft, request.LaneNumbers, dateIsValid ? request.SeedingDate : null, ofCycle, lots);
        check.Check(lanes is not null && laneHint is null, SeedingValues.LanesField);
        var reason = check.Required(request.Reason, SeedingValues.ReasonField, SeedingValues.MaxReasonLength);
        if (!check.IsValid)
        {
            return (null, laneHint is not null && check.Invalid is [SeedingValues.LanesField] ? AppError.Invalid(SeedingValues.LanesField, laneHint) : AppError.InvalidFields(check.Invalid));
        }

        return (new SeedingFields(request.SeedingDate!.Value, lot!, wanted!.Value, lanes!, reason), null);
    }

    /// <param name="date">The corrected date when it is valid, else null: the lanes are then checked only as lanes of the raft.</param>
    private static (IReadOnlyList<Lane>? Lanes, string? Hint) Lanes(
        SeedingRecord record, Raft raft, IReadOnlyList<int>? numbers, DateOnly? date, IReadOnlyList<SeedingRecord> ofCycle, IReadOnlyList<HarvestLot> lots)
    {
        if (numbers is null || numbers.Count == 0 || numbers.Distinct().Count() != numbers.Count)
        {
            return (null, null);
        }

        var own = record.Lanes.Select(l => l.LaneId).ToHashSet();
        var lanes = raft.Lanes.Where(l => numbers.Contains(l.LaneNumber) && (own.Contains(l.Id) || l.StatusId == StatusIds.Lane.InUse)).ToList();
        if (lanes.Count != numbers.Count || date is not { } on)
        {
            return (lanes.Count == numbers.Count ? lanes : null, null);
        }

        var cycle = record.Cycle;
        var others = ofCycle.Where(s => s.Id != record.Id).SelectMany(s => s.Lanes.Select(l => new LaneSeeding(cycle.Id, l.LaneId, s.SeedingDate, l.SeededPercent, s.CreatedAt, s.Id))).ToList();
        var harvests = lots.SelectMany(lot => lot.Lanes.Select(l => new LaneHarvest(cycle.Id, l.LaneId, lot.HarvestDate, l.IsFinished, l.HarvestedPercent, lot.HarvestedAt, lot.Id))).ToList();
        var states = LaneStateRules.StatesOn(on, new CycleSpan(cycle.Id, cycle.RaftId, cycle.StartDate, cycle.CloseDate), raft.Lanes.Select(l => l.Id), others, harvests);

        // The seeding was marked complete before that day: only lanes still Not seeded then (BR-CYCLE-02).
        var completed = cycle.SeedingCompletedOn is { } completedOn && completedOn < on;
        var toCheck = on == record.SeedingDate ? lanes.Where(l => !own.Contains(l.Id)) : lanes;
        var refused = toCheck.Where(l => !SeedingLaneRules.CanBeSeeded(states[l.Id], completed)).Select(l => l.LaneNumber).Order().ToList();
        return (lanes, refused.Count == 0 ? null : $"Lanes {string.Join(", ", refused)} cannot be seeded on {on:dd/MM/yyyy}.");
    }
}
