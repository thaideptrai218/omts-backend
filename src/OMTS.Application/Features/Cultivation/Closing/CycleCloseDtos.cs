using OMTS.Application.Features.Cultivation.Cycles;

namespace OMTS.Application.Features.Cultivation.Closing;

/// <summary>CYCLE-06 step 4/5: the assessment, the Zone Manager's answer to MSG-CYCLE-07 for an early close, and the version of the cycle read with the preview.</summary>
/// <param name="Assessment">Required free text, at most 2000 characters (CYCLE-06.0.E1).</param>
/// <param name="OwnerApproved">
/// A Zone Manager confirms that the Cooperative Owner approved an early close face to face (MSG-CYCLE-07); without it an early close answers 409 with
/// <c>confirmFlag: "ownerApproved"</c> and changes nothing. The Owner never needs it; a normal close ignores it.
/// </param>
public sealed record CloseCycleRequest(string? Assessment, string? RowVersion, bool OwnerApproved = false);

/// <summary>Why a close is early (BR-CYCLE-07): no seeding session, or seeded lanes that are not Harvested.</summary>
public enum EarlyCloseReason
{
    NoSeedingSession,
    LanesNotHarvested,
}

/// <summary>
/// The cycle summary (BR-CYCLE-06), worked out from the records of the cycle each time it is shown and never stored. After the close it is read as of the close date,
/// and only TRACE-03 can still change the records it comes from; the MSG-CYCLE-06 notification and the audit row of the close keep the figures of the close day.
/// </summary>
/// <param name="AsOf">The close date of a Closed cycle; today for an Open one (the day it would close).</param>
/// <param name="SeedHung">The total quantity of the seeding sessions, in <paramref name="SeedUnit"/>.</param>
/// <param name="SeedUnit">The unit of the seed lots hung; null with no session.</param>
/// <param name="KgHarvested">The total estimated kg of the harvest lots of the cycle, not the weight confirmed at the port.</param>
/// <param name="LastHarvestDate">The date of the last harvest lot, or null; compared with <paramref name="ExpectedHarvestDate"/>.</param>
/// <param name="DaysOfFarming">From the start date to the last harvest lot, or to the close date (today for an Open cycle) without one; both days count.</param>
/// <param name="ExpectedSeedQuantity">Plan estimate next to <paramref name="SeedHung"/>; null: Not planned.</param>
/// <param name="ExpectedHarvestDate">Plan estimate next to <paramref name="LastHarvestDate"/>; null: Not planned.</param>
/// <param name="TargetYieldKg">Plan estimate next to <paramref name="KgHarvested"/>; null: Not planned.</param>
/// <param name="ClosedEarly">For a Closed cycle, whether it was closed early; for an Open one, whether closing it today would be early.</param>
/// <param name="EarlyReason">Why <paramref name="ClosedEarly"/>, or null.</param>
/// <param name="NotHarvestedLanes">The seeded lanes not Harvested on <paramref name="AsOf"/>, by number: after an early close they ended with no harvest weight.</param>
/// <param name="Assessment">The assessment of a Closed cycle, else null.</param>
public sealed record CycleSummary(
    Guid CycleId,
    string RaftCode,
    DateOnly StartDate,
    DateOnly? CloseDate,
    DateOnly AsOf,
    int SeedingSessionCount,
    decimal SeedHung,
    string? SeedUnit,
    int HarvestLotCount,
    decimal KgHarvested,
    DateOnly? LastHarvestDate,
    int DaysOfFarming,
    decimal? ExpectedSeedQuantity,
    DateOnly? ExpectedHarvestDate,
    decimal? TargetYieldKg,
    bool ClosedEarly,
    EarlyCloseReason? EarlyReason,
    IReadOnlyList<int> NotHarvestedLanes,
    string? Assessment,
    decimal HarvestedCages = 0m,
    decimal? RemainingKg = null);

/// <summary>What CYCLE-06 shows before the close (steps 1, 2 and 4).</summary>
/// <param name="Summary">The summary if the cycle closed today.</param>
/// <param name="IsEarly">The close would be early (CYCLE-06.1).</param>
/// <param name="NeedsOwnerApproval">Early and the caller is not the Cooperative Owner: the close must send <c>ownerApproved: true</c>.</param>
/// <param name="EarlyCloseMessage">MSG-CYCLE-07 with its reason, or null for a normal close. The Owner is asked only to confirm (BR-CYCLE-07).</param>
/// <param name="ConfirmMessage">MSG-CYCLE-05, the last confirmation.</param>
/// <param name="CycleRowVersion">The version to send with the close.</param>
public sealed record CycleClosePreview(
    CycleSummary Summary,
    bool IsEarly,
    bool NeedsOwnerApproval,
    string? EarlyCloseMessage,
    string? EarlyCloseMessageCode,
    string ConfirmMessage,
    string ConfirmMessageCode,
    string CycleRowVersion);

/// <summary>Answer of CYCLE-06: the Closed cycle and its summary on the close day.</summary>
public sealed record CycleClosed(CycleView Cycle, CycleSummary Summary);
