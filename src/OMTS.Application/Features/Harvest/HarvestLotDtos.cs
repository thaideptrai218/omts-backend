using OMTS.Application.Common.Messages;
using OMTS.Application.Features.Cultivation.Cycles;
using OMTS.Application.Features.Cultivation.Progress;
using OMTS.Domain.Enums;

using OMTS.Application.Features.Workforce;

namespace OMTS.Application.Features.Harvest;

/// <summary>HARVEST-01.0: the photos come beside it. The harvest date and time are the system's, never sent.</summary>
/// <param name="LaneNumbers">The lanes of the harvest, each given once; null when a value could not be read.</param>
/// <param name="FinishedLaneNumbers">Those of them with no oysters left to harvest; none is fine; null when a value could not be read.</param>
/// <param name="EstimatedKg">Without it the system works it out: cages times the average kg per cage.</param>
/// <param name="EstimatedKgIsUnreadable">A value was sent that is not a number, so it does not mean "work it out".</param>
/// <param name="OtherFilePart">A file came in a part not named <c>photos</c>: refused after the access check, so no photo is dropped without a word.</param>
/// <param name="ProductLines">The share of each product on the lot (null: the form sent none, the lot is not split per product). With lines, the cages and estimated kg of the lot are their sums and any <c>CageCount</c> or <c>EstimatedKg</c> sent is ignored.</param>
/// <param name="ProductLinesAreUnreadable">A line value was sent that is not a number.</param>
/// <param name="HarvestPlanLineId">The line of a harvest plan (KHT) the lot is recorded from: the lot is of the whole raft of the line, so it takes every lane of the raft that can be harvested when it is recorded (the lanes sent are ignored), and the plan moves on with the lot.</param>
/// <param name="HarvestPlanLineIsUnreadable">A value was sent for the plan line that is not an id.</param>
/// <param name="ConfirmOverPlan">Kept so older clients still bind: the cycle target is a plan, not a cap (owner decision 12/11/2026), so nothing asks for it and it is ignored.</param>
/// <param name="OverPlanReason">As <paramref name="ConfirmOverPlan"/>: accepted and ignored.</param>
/// <param name="Crew">The employees who worked on the trip (decision of 12/11/2026), optional: null when none was sent (a correction then leaves the crew as it is); an empty list clears it.</param>
/// <param name="CrewUnreadable">An id was sent that is not a GUID.</param>
public sealed record RecordHarvestRequest(
    IReadOnlyList<int>? LaneNumbers, IReadOnlyList<int>? FinishedLaneNumbers, decimal? CageCount, decimal? EstimatedKg, bool EstimatedKgIsUnreadable = false, bool OtherFilePart = false,
    bool ConfirmOverPlan = false, string? OverPlanReason = null, IReadOnlyList<Guid>? Crew = null, bool CrewUnreadable = false,
    IReadOnlyDictionary<int, decimal>? LanePercents = null, bool LanePercentsUnreadable = false,
    IReadOnlyList<HarvestProductLineInput>? ProductLines = null, bool ProductLinesAreUnreadable = false, Guid? HarvestPlanLineId = null, bool HarvestPlanLineIsUnreadable = false);

/// <summary>The harvest plan (KHT) a lot was recorded from; the lot code does not change.</summary>
public sealed record HarvestLotPlanRef(Guid Id, string Code);

/// <summary>A seed lot that went into the cycle of a lot: the line of a purchase receipt, with its code (receipt code and line number) and the item.</summary>
public sealed record HarvestLotSeedLotRef(Guid ReceiptId, string ReceiptCode, int LineNumber, string SeedLotCode, string ItemCode, string ItemName);

/// <summary>A record a lot links to, by id and code: a nursery batch, a sales order or a sales invoice.</summary>
public sealed record HarvestLotLinkRef(Guid Id, string Code);

/// <summary>Where a lot came from and the documents that touched it, for the links of the lot screen.</summary>
/// <param name="SeedLots">The seed lots of the seeding records and of the nursery batch of the cycle, each once.</param>
/// <param name="NurseryBatch">The nursery batch the cycle was started from; null for a cycle started the old way.</param>
/// <param name="SalesOrders">The orders (DH) the harvest plan of the lot serves, by code; empty for a lot outside a plan.</param>
/// <param name="ClosedByInvoice">The invoice whose sold-whole-lot tick closed the lot; null otherwise.</param>
public sealed record HarvestLotReferences(
    IReadOnlyList<HarvestLotSeedLotRef> SeedLots, HarvestLotLinkRef? NurseryBatch, IReadOnlyList<HarvestLotLinkRef> SalesOrders, HarvestLotLinkRef? ClosedByInvoice)
{
    public static readonly HarvestLotReferences None = new([], null, [], null);
}

/// <summary>
/// The kg of one product of a lot on the product ledger. A reversal counts under the kind of the row it undoes.
/// </summary>
/// <param name="HarvestedKg">What the lot gave of the product: its weighed kg; 0 until the lot is weighed.</param>
/// <param name="SoldKg">The kg of the product invoiced on the lot (sales that stand).</param>
/// <param name="RemainingKg">The balance of the product on the lot: harvested, less what was sold and written off, plus what came back, less what the close took off.</param>
/// <param name="ReturnedKg">The kg of the product that came back for sale through sales returns (already part of <paramref name="RemainingKg"/>).</param>
public sealed record HarvestLotProductBalance(Guid ProductId, string ProductCode, string ProductName, decimal HarvestedKg, decimal SoldKg, decimal RemainingKg, decimal ReturnedKg);

/// <summary>A product line of a lot, with the code of the product.</summary>
/// <param name="Kg">The estimated kg of the line.</param>
/// <param name="WeighedKg">The kg of the weighing; null until the lot is weighed.</param>
public sealed record HarvestLotProductLineView(Guid ProductId, string ProductCode, string ProductName, decimal CageCount, decimal Kg, decimal? WeighedKg = null);

/// <summary>The kg of a product on a lot for sale: what the lot gave and what is left on the ledger (sales and disposals taken off).</summary>
public sealed record HarvestLotProductStock(Guid ProductId, string ProductCode, string ProductName, decimal HarvestedKg, decimal RemainingKg);

/// <summary>A photo of a lot, for staff.</summary>
/// <param name="Id">The id of the photo row, as on the other steps (<c>photos[{id,url}]</c>).</param>
/// <param name="Key">The storage key: what <c>keepPhotoKeys</c> names when a correction keeps the photo.</param>
/// <param name="Url">A signed link that expires; read the lot again for a new one.</param>
public sealed record HarvestLotPhotoView(string Key, int SortOrder, string Url, Guid Id = default);

/// <summary>The share of a lane a lot harvested (owner decision of 12/11/2026); null when nobody entered it.</summary>
public sealed record HarvestLanePercentView(int LaneNumber, decimal? Percent);

/// <summary>A lot on the Harvest lots screen (SCR-HARVEST-01).</summary>
/// <param name="LaneNumbers">In number order.</param>
/// <param name="FinishedLaneNumbers">The lanes this lot marked finished, in number order.</param>
/// <param name="OriginMissing">As on <see cref="HarvestLotView"/>.</param>
/// <param name="CanPublish">As on <see cref="HarvestLotView"/>.</param>
/// <param name="CanClose">Show Close lot, as on <see cref="HarvestLotView"/>.</param>
public sealed record HarvestLotListItem(
    Guid Id,
    string LotCode,
    DateTimeOffset HarvestedAt,
    DateOnly HarvestDate,
    IReadOnlyList<int> LaneNumbers,
    IReadOnlyList<int> FinishedLaneNumbers,
    decimal CageCount,
    decimal KgPerCage,
    decimal EstimatedKg,
    decimal InvoicedKg,
    decimal RemainingKg,
    int StatusId,
    string Status,
    HarvestLotOriginState OriginState,
    IReadOnlyList<OriginGap> OriginMissing,
    bool CanPublish,
    bool CanCorrect,
    bool CanRemove,
    bool CanClose,
    string RowVersion,
    IReadOnlyList<HarvestLotProductLineView> ProductLines,
    HarvestLotPlanRef? HarvestPlan,
    IReadOnlyList<CrewMemberView>? Crew = null);

/// <summary>A harvest lot with its figures (BR-HARVEST-07): the record a notification opens.</summary>
/// <param name="Photos">In slot order.</param>
/// <param name="CanCorrect">The caller may correct the lot now: its cycle is Open, its raft and zone are In Use, and the caller recorded it or is the Owner (BR-HARVEST-05).</param>
/// <param name="CanRemove">The same, and the lot is on no sales invoice.</param>
/// <param name="CanClose">The caller may close the lot by hand now (HARVEST-02): it is for sale and the caller is a Zone Manager of its zone or the Owner, whatever the state of the cycle.</param>
/// <param name="OriginMissing">What the provenance page would miss (BR-TRACE-01: lanes without a seed lot); empty when the lot has all its origin information.</param>
/// <param name="CanPublish">
/// Show Publish (TRACE-01.2): a Not published lot on a sales invoice that is not cancelled, or a Withdrawn lot for the Cooperative Owner, in a zone where the caller publishes. The
/// publish still answers MSG-TRACE-01 while <paramref name="OriginMissing"/> is not empty.
/// </param>
/// <param name="WeighedKg">The kg of the weighing (the sum of the weighed kg of the lines); null until the lot is weighed. The figures count from it once it is set.</param>
/// <param name="WeighedAt">When the lot was weighed.</param>
/// <param name="WeighedByName">Who weighed it last.</param>
/// <param name="CanWeigh">The caller may weigh the lot (or weigh it again) now: it is Awaiting weighing, Open or Reopened, and the caller is a Zone Manager of its zone or the Owner.</param>
/// <param name="CanReopen">The caller may reopen the lot by hand now: it is Closed Unsold, Spoiled, by the time limit or with nothing left (not by an invoice), and the caller is a Zone Manager of its zone or the Owner.</param>
/// <param name="References">The seed lots, nursery batch, orders and closing invoice the lot links to; read with the lot, so the list rows have none.</param>
/// <param name="ProductStocks">The kg of each product of the lot on the product ledger, in line order: the lines of the lot first, then any other product the ledger holds on it.</param>
public sealed record HarvestLotView(
    Guid Id,
    string LotCode,
    Guid CycleId,
    Guid RaftId,
    string RaftCode,
    Guid ZoneId,
    string ZoneCode,
    DateTimeOffset HarvestedAt,
    DateOnly HarvestDate,
    IReadOnlyList<int> LaneNumbers,
    IReadOnlyList<int> FinishedLaneNumbers,
    decimal CageCount,
    decimal KgPerCage,
    decimal EstimatedKg,
    HarvestLotFigures Figures,
    int StatusId,
    string Status,
    DateOnly? ReopenedOn,
    DateOnly? CloseDate,
    decimal? LeftoverKg,
    HarvestLotCloseReason? CloseReason,
    HarvestLotOriginState OriginState,
    DateTimeOffset? PublishedAt,
    DateTimeOffset? WithdrawnAt,
    IReadOnlyList<OriginGap> OriginMissing,
    bool CanPublish,
    IReadOnlyList<HarvestLotPhotoView> Photos,
    Guid RecordedByAccountId,
    string? RecordedByName,
    bool CanCorrect,
    bool CanRemove,
    bool CanClose,
    string RowVersion,
    IReadOnlyList<HarvestLotProductLineView> ProductLines,
    HarvestLotPlanRef? HarvestPlan,
    string? OverPlanReason = null,
    IReadOnlyList<CrewMemberView>? Crew = null,
    IReadOnlyList<HarvestLanePercentView>? LanePercents = null,
    decimal? WeighedKg = null,
    DateTimeOffset? WeighedAt = null,
    string? WeighedByName = null,
    bool CanWeigh = false,
    bool CanReopen = false,
    HarvestLotReferences? References = null,
    IReadOnlyList<HarvestLotProductBalance>? ProductStocks = null);

/// <summary>Answer of a recorded harvest.</summary>
/// <param name="EverySeededLaneHarvested">Every seeded lane of the cycle shows Harvested now: the cycle can be closed normally (BR-CYCLE-07).</param>
/// <param name="CycleRowVersion">The new version of the cycle, as every lot moves it.</param>
/// <param name="Message">MSG-GEN-05 "Harvest lot saved.".</param>
/// <param name="MessageCode">The code of <paramref name="Message"/>.</param>
/// <param name="OrderCagesTotal">The cages the line of the harvest plan expects; set when the lot is recorded from a plan line that expects cages (null otherwise and after a correction).</param>
/// <param name="OrderCagesHarvested">The cages of this lot, to set against <paramref name="OrderCagesTotal"/>.</param>
/// <param name="OrderComplete">The lot reaches the cages the plan line expects (e.g. 15/15). Going over is only a warning.</param>
/// <param name="CrewAssignment">The automatic work assignment of the crew newly named by this save; null when no employee was added.</param>
public sealed record HarvestLotSaved(
    HarvestLotView Lot, bool EverySeededLaneHarvested, string CycleRowVersion, string Message, string MessageCode, IReadOnlyList<Features.Farming.FarmingWarning>? Warnings = null,
    decimal? OrderCagesTotal = null, decimal? OrderCagesHarvested = null, bool OrderComplete = false,
    [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)] Workforce.CrewAssignmentResult? CrewAssignment = null);

/// <summary>A lane on the Harvest lots screen, with its state today (BR-FARM-03).</summary>
/// <param name="CanHarvest">The lane can be chosen for a new lot now: In Use, Growing or Being harvested, on a raft that can record.</param>
/// <param name="Reason">Why <paramref name="CanHarvest"/> is false, or null.</param>
/// <param name="ReasonCode">One of <c>LaneRemoved</c>, <c>LaneDamaged</c>, <c>LaneOutOfService</c>, <c>NoOpenCycle</c>, <c>ReadOnly</c>, <c>NoPermission</c>, <c>NotSeeded</c>, <c>AlreadyHarvested</c>; null when the lane can be harvested.</param>
/// <param name="IsDamaged">The lane is damaged and waits for a repair (decision of 12/11/2026): it keeps its state and cannot be chosen.</param>
/// <param name="IsOutOfService">The lane was put out of service: the web hides it from the pickers.</param>
/// <param name="CanFinish">The lane is Being harvested and the caller may finish it with <c>finish-harvest</c>.</param>
/// <param name="LotCount">Lots of the Open cycle that include the lane.</param>
/// <param name="LastHarvestDate">The latest harvest date of those lots, or null.</param>
/// <param name="SeedLotCodes">Codes (PN000001-1) of the seed lots hung on the lane in the cycle, in code order.</param>
/// <param name="HarvestedCages">
/// Cages harvested from the lane so far: each lot of the lane counts its cages divided by the number of lanes of that lot, as a lot is recorded for several lanes together.
/// </param>
public sealed record HarvestLaneView(
    Guid Id,
    int LaneNumber,
    int StatusId,
    string Status,
    LaneState State,
    bool CanHarvest,
    string? Reason = null,
    string? ReasonCode = null,
    bool CanFinish = false,
    int LotCount = 0,
    DateOnly? LastHarvestDate = null,
    IReadOnlyList<string>? SeedLotCodes = null,
    decimal HarvestedCages = 0m,
    bool IsDamaged = false,
    bool IsOutOfService = false,
    decimal? LineLengthM = null,
    decimal SeededPercent = 0m,
    decimal? HarvestedPercent = null,
    string? LastModifiedByName = null,
    DateTimeOffset? LastModifiedAt = null,
    IReadOnlyList<LaneProgressChangeView>? ProgressHistory = null);

/// <summary>The Harvest lots screen of a raft (SCR-HARVEST-01).</summary>
/// <param name="Cycle">The Open cycle, or null: no lot can be recorded.</param>
/// <param name="AverageKgPerCage">The current setting, to work out the estimated kg on the form.</param>
/// <param name="CanRecordHarvest">Show Record harvest: at least one lane is Growing or Being harvested, and the caller may record.</param>
/// <param name="Lanes">Lanes In Use, and Removed lanes that have records in the cycle shown, in number order.</param>
/// <param name="Lots">The lots of the Open cycle: lots for sale first, then the latest harvest first.</param>
/// <param name="CycleProductId">The product the Open cycle farms; null with no Open cycle or a cycle with no product. <paramref name="CycleProductName"/> is its name.</param>
public sealed record HarvestLotsScreen(
    Guid RaftId,
    string RaftCode,
    CycleListItem? Cycle,
    decimal AverageKgPerCage,
    bool CanRecordHarvest,
    IReadOnlyList<HarvestLaneView> Lanes,
    IReadOnlyList<HarvestLotListItem> Lots,
    decimal HarvestedCages = 0m,
    decimal HarvestedKg = 0m,
    decimal? TargetYieldKg = null,
    decimal? RemainingKg = null,
    decimal? TargetPercent = null,
    bool OverTarget = false,
    Guid? CycleProductId = null,
    string? CycleProductName = null);

/// <summary>The harvest settings (HARVEST-03).</summary>
public sealed record HarvestSettingsView(decimal AverageKgPerCage, int DaysBeforeAutoClose, string RowVersion);

/// <summary>HARVEST-03 step 2. <paramref name="RowVersion"/> is the version of the cooperative record.</summary>
public sealed record UpdateHarvestSettingsRequest(decimal? AverageKgPerCage, int? DaysBeforeAutoClose, string? RowVersion);

/// <summary>Answer of saved settings.</summary>
/// <param name="Message">MSG-GEN-05 "Harvest settings saved.".</param>
/// <param name="MessageCode">The code of <paramref name="Message"/>.</param>
public sealed record HarvestSettingsSaved(HarvestSettingsView Settings, string Message, string MessageCode);

/// <summary>What to ask before saving new days before automatic close (HARVEST-03 step 4).</summary>
/// <param name="LotCount">The lots for sale the next daily close check would close with these days.</param>
/// <param name="Lowered">These days are fewer than the current ones.</param>
/// <param name="Message">MSG-HARVEST-03 when <paramref name="Lowered"/>, else null: only the plain confirmation.</param>
/// <param name="MessageCode">The code of <paramref name="Message"/>.</param>
public sealed record AutoClosePreview(int Days, int LotCount, bool Lowered, string? Message, string? MessageCode);
