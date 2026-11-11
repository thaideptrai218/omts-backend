namespace OMTS.Application.Features.Harvest;

/// <summary>The three groupings of the yield report (HARVEST-04 step 1).</summary>
public enum HarvestYieldGrouping
{
    Raft,
    Zone,
    Cycle,
}

/// <summary>HARVEST-04 step 2: the filters of the yield report and of its export.</summary>
/// <param name="From">First harvest date of the period (Vietnam date); default the first day of the month of <paramref name="To"/>.</param>
/// <param name="To">Last harvest date of the period, included; default the last day of the current month.</param>
/// <param name="ZoneIds">Zones the caller can access; none: every zone the caller can access (the Owner: all zones together).</param>
/// <param name="GroupBy"><c>raft</c> (default), <c>zone</c> or <c>cycle</c>, any letter case.</param>
public sealed record HarvestYieldQuery(DateOnly? From = null, DateOnly? To = null, Guid[]? ZoneIds = null, string? GroupBy = null);

/// <summary>One lot of a group (HARVEST-04.1), with its figures of BR-HARVEST-07 worked out now.</summary>
/// <param name="InPeriod">Its harvest date is in the period; false only for the other lots of a cycle that has a lot in the period (grouped by cycle).</param>
/// <param name="SoldKg">Invoiced kg minus returned kg.</param>
/// <param name="LostKg">Leftover kg at the close plus the discarded kg: the shrinkage.</param>
/// <param name="DifferenceKg">Estimated kg minus sold kg minus lost kg; below 0 when more was sold than estimated.</param>
/// <param name="NotFinal">The lot is still for sale (Open or Reopened): its figures may still change.</param>
public sealed record HarvestYieldLot(
    Guid Id,
    string LotCode,
    DateOnly HarvestDate,
    bool InPeriod,
    Guid RaftId,
    string RaftCode,
    Guid ZoneId,
    string ZoneCode,
    Guid CycleId,
    int StatusId,
    string Status,
    decimal EstimatedKg,
    decimal InvoicedKg,
    decimal ReturnedKg,
    decimal SoldKg,
    decimal LostKg,
    decimal DifferenceKg,
    bool NotFinal,
    decimal CageCount = 0m,
    decimal RemainingKg = 0m);

/// <summary>One raft, zone or cycle of the report: each figure is the total of the same figure of its lots (POST-1).</summary>
/// <param name="Key">The id of the raft, zone or cycle.</param>
/// <param name="Label">What the export shows: the raft code, "zone code - zone name", or "raft code from start date".</param>
/// <param name="RaftId">Grouped by raft or cycle; null grouped by zone.</param>
/// <param name="CycleId">Grouped by cycle; else null, like the other cycle fields.</param>
/// <param name="CycleStatus">The cycle's status now ("Open" or "Closed"): an Open cycle can still get lots.</param>
/// <param name="NotFinal">A lot of the group is still for sale (MSG-HARVEST-04).</param>
/// <param name="Lots">The lots of the group, oldest harvest first.</param>
public sealed record HarvestYieldGroup(
    Guid Key,
    string Label,
    Guid ZoneId,
    string ZoneCode,
    string ZoneName,
    Guid? RaftId,
    string? RaftCode,
    Guid? CycleId,
    DateOnly? CycleStartDate,
    DateOnly? CycleCloseDate,
    string? CycleStatus,
    int LotCount,
    decimal EstimatedKg,
    decimal SoldKg,
    decimal LostKg,
    decimal DifferenceKg,
    bool NotFinal,
    IReadOnlyList<HarvestYieldLot> Lots,
    decimal HarvestedCages = 0m,
    decimal HarvestedKg = 0m,
    decimal? TargetYieldKg = null,
    decimal? RemainingKg = null,
    decimal? SeedQuantity = null,
    string? SeedUnit = null,
    decimal AvailableKg = 0m);

/// <summary>The totals of every lot of the report.</summary>
/// <param name="HarvestedCages">The cages of every lot of the report.</param>
public sealed record HarvestYieldTotals(int LotCount, decimal EstimatedKg, decimal SoldKg, decimal LostKg, decimal DifferenceKg, bool NotFinal, decimal HarvestedCages = 0m);

/// <summary>HARVEST-04 step 3 (SCR-HARVEST-03).</summary>
/// <param name="From">The period applied, defaults resolved.</param>
/// <param name="Groups">By zone code, then raft code, then cycle start.</param>
/// <param name="NotFinalNotice">MSG-HARVEST-04 when a group is not final, else null.</param>
/// <param name="Message">MSG-GEN-10 when no lot is in the selection (HARVEST-04.0.E1), else null.</param>
public sealed record HarvestYieldReport(
    DateOnly From,
    DateOnly To,
    HarvestYieldGrouping GroupBy,
    IReadOnlyList<HarvestYieldGroup> Groups,
    HarvestYieldTotals Totals,
    string? NotFinalNotice,
    string? NotFinalNoticeCode,
    string? Message,
    string? MessageCode);
