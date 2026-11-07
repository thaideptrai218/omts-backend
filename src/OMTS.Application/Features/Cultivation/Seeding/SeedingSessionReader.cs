using OMTS.Application.Common;
using OMTS.Application.Common.Interfaces.Persistence;
using OMTS.Application.Common.Results;
using OMTS.Application.Features.Cultivation.Progress;
using OMTS.Application.Features.Farming;
using OMTS.Application.Features.Purchasing.Receipts;
using OMTS.Domain.Constants;
using OMTS.Domain.Entities.Cultivation;
using OMTS.Domain.Entities.Farming;
using OMTS.Domain.Entities.Purchasing;

namespace OMTS.Application.Features.Cultivation.Seeding;

/// <summary>The checked fields of a session.</summary>
internal sealed record SeedingFields(DateOnly Date, PurchaseReceiptLine Lot, decimal Quantity, IReadOnlyList<Lane> Lanes, string Reason);

/// <summary>What a session sends, new or corrected.</summary>
internal sealed record SeedingInput(DateOnly? Date, Guid? SeedLotId, IReadOnlyList<int>? LaneNumbers, decimal? Quantity);

/// <summary>
/// CYCLE-02.0 step 5, shared by a new and a corrected session: the date against the cycle, the seed lot and today, the seed lot itself, the quantity and the lanes
/// (BR-CYCLE-02, GBR-04). Every invalid field is listed (MSG-GEN-01).
/// </summary>
public sealed class SeedingSessionReader(ISeedLotRepository lots, IRaftRepository rafts, LaneStateReader laneStates, TimeProvider timeProvider)
{
    /// <param name="current">The session being corrected. Its own seed lot and its own lanes stay allowed: only what the correction adds is checked as a new choice.</param>
    /// <param name="reasonRequired">A correction needs a reason, kept in the audit log.</param>
    internal async Task<(SeedingFields? Fields, AppError? Error)> ReadAsync(
        CultivationCycle cycle, SeedingInput input, SeedingRecord? current, string? reason, bool reasonRequired, CancellationToken cancellationToken)
    {
        var today = timeProvider.Today();
        var lot = await LotAsync(input.SeedLotId, current, cancellationToken);
        var raft = await rafts.GetForEditAsync(cycle.RaftId, cancellationToken) ?? throw new InvalidOperationException($"Raft {cycle.RaftId} not found.");

        var check = new FieldCheck();
        check.Check(input.Date is { } date && date >= cycle.StartDate && date <= today && (lot is null || date >= lot.Receipt.ReceiptDate), SeedingValues.DateField);
        check.Check(lot is not null, SeedingValues.SeedLotField);
        check.Check(input.Quantity is { } quantity && quantity > 0 && quantity <= PurchaseReceiptValidator.MaxQuantity && PurchaseReceiptValidator.HasAllowedDecimals(quantity), SeedingValues.QuantityField);
        var (lanes, laneHint) = await LanesAsync(raft, cycle, input.LaneNumbers, current, today, cancellationToken);
        check.Check(lanes is not null && laneHint is null, SeedingValues.LanesField);
        var trimmedReason = reasonRequired ? check.Required(reason, SeedingValues.ReasonField, SeedingValues.MaxReasonLength) : string.Empty;
        if (!check.IsValid)
        {
            return (null, laneHint is not null && check.Invalid is [SeedingValues.LanesField] ? AppError.Invalid(SeedingValues.LanesField, laneHint) : AppError.InvalidFields(check.Invalid));
        }

        return (new SeedingFields(input.Date!.Value, lot!, input.Quantity!.Value, lanes!, trimmedReason), null);
    }

    /// <summary>GBR-04: a Seed line of a Completed receipt whose whole quantity has not been returned. Lots of a discontinued item or supplier stay: GBR-01 is not cited by CYCLE-02, and the seed is already bought.</summary>
    internal async Task<PurchaseReceiptLine?> LotAsync(Guid? seedLotId, SeedingRecord? current, CancellationToken cancellationToken)
    {
        if (seedLotId is not { } id)
        {
            return null;
        }

        if (current?.SeedLotLineId == id)
        {
            return current.SeedLotLine;
        }

        var lot = await lots.GetForSeedingAsync(id, cancellationToken);
        return lot is not null && PurchaseReceiptViews.IsSeedLot(lot.Receipt, lot) && await lots.ReturnedAsync(id, cancellationToken) < lot.Quantity ? lot : null;
    }

    /// <summary>One or more lanes of the raft, each In Use and given once. Lanes the correction adds must be free to seed today, as for a new session.</summary>
    private async Task<(IReadOnlyList<Lane>? Lanes, string? Hint)> LanesAsync(
        Raft raft, CultivationCycle cycle, IReadOnlyList<int>? numbers, SeedingRecord? current, DateOnly today, CancellationToken cancellationToken)
    {
        if (numbers is null || numbers.Count == 0 || numbers.Distinct().Count() != numbers.Count)
        {
            return (null, null);
        }

        var lanes = raft.Lanes.Where(l => numbers.Contains(l.LaneNumber) && l.StatusId == StatusIds.Lane.InUse).ToList();
        if (lanes.Count != numbers.Count)
        {
            return (null, null);
        }

        var states = (await laneStates.ReadAsync(raft, today, cancellationToken)).States;
        var isOwn = (current?.Lanes ?? []).Select(l => l.LaneId).ToHashSet();
        var completed = cycle.SeedingCompletedOn is not null;
        var refused = lanes.Where(l => !isOwn.Contains(l.Id) && !SeedingLaneRules.CanBeSeeded(states[l.Id], completed)).Select(l => l.LaneNumber).Order().ToList();
        return (lanes, refused.Count == 0 ? null : $"Lanes {string.Join(", ", refused)} cannot be seeded now.");
    }
}
