using OMTS.Application.Common.Messages;
using OMTS.Application.Features.Cultivation.Cycles;
using OMTS.Application.Features.Cultivation.Progress;
using OMTS.Domain.Enums;

namespace OMTS.Application.Features.Harvest;

/// <summary>HARVEST-01.0: the photos come beside it. The harvest date and time are the system's, never sent.</summary>
/// <param name="LaneNumbers">The lanes of the harvest, each given once; null when a value could not be read.</param>
/// <param name="FinishedLaneNumbers">Those of them with no oysters left to harvest; none is fine; null when a value could not be read.</param>
/// <param name="EstimatedKg">Without it the system works it out: cages times the average kg per cage.</param>
/// <param name="EstimatedKgIsUnreadable">A value was sent that is not a number, so it does not mean "work it out".</param>
/// <param name="OtherFilePart">A file came in a part not named <c>photos</c>: refused after the access check, so no photo is dropped without a word.</param>
/// <param name="ProductLines">The share of each product on the lot (null: the form sent none, the lot is not split per product). With lines, the cages and estimated kg of the lot are their sums and any <c>CageCount</c> or <c>EstimatedKg</c> sent is ignored.</param>
/// <param name="ProductLinesAreUnreadable">A line value was sent that is not a number.</param>
public sealed record RecordHarvestRequest(
    IReadOnlyList<int>? LaneNumbers, IReadOnlyList<int>? FinishedLaneNumbers, int? CageCount, decimal? EstimatedKg, bool EstimatedKgIsUnreadable = false, bool OtherFilePart = false,
    IReadOnlyList<HarvestProductLineInput>? ProductLines = null, bool ProductLinesAreUnreadable = false);

/// <summary>A product line of a lot, with the code of the product.</summary>
public sealed record HarvestLotProductLineView(Guid ProductId, string ProductCode, string ProductName, int CageCount, decimal Kg);

/// <summary>The kg of a product on a lot for sale: what the lot gave and what is left on the ledger (sales and disposals taken off).</summary>
public sealed record HarvestLotProductStock(Guid ProductId, string ProductCode, string ProductName, decimal HarvestedKg, decimal RemainingKg);

/// <summary>A photo of a lot, for staff.</summary>
/// <param name="Key">The storage key.</param>
/// <param name="Url">A signed link that expires; read the lot again for a new one.</param>
public sealed record HarvestLotPhotoView(string Key, int SortOrder, string Url);

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
    int CageCount,
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
    IReadOnlyList<HarvestLotProductLineView> ProductLines);

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
    int CageCount,
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
    IReadOnlyList<HarvestLotProductLineView> ProductLines);

/// <summary>Answer of a recorded harvest.</summary>
/// <param name="EverySeededLaneHarvested">Every seeded lane of the cycle shows Harvested now: the cycle can be closed normally (BR-CYCLE-07).</param>
/// <param name="CycleRowVersion">The new version of the cycle, as every lot moves it.</param>
/// <param name="Message">MSG-GEN-05 "Harvest lot saved.".</param>
/// <param name="MessageCode">The code of <paramref name="Message"/>.</param>
public sealed record HarvestLotSaved(HarvestLotView Lot, bool EverySeededLaneHarvested, string CycleRowVersion, string Message, string MessageCode);

/// <summary>A lane on the Harvest lots screen, with its state today (BR-FARM-03). It never shows kg (AC-03).</summary>
/// <param name="CanHarvest">The lane can be chosen for a new lot now: In Use, Growing or Being harvested, on a raft that can record.</param>
public sealed record HarvestLaneView(Guid Id, int LaneNumber, int StatusId, string Status, LaneState State, bool CanHarvest);

/// <summary>The Harvest lots screen of a raft (SCR-HARVEST-01).</summary>
/// <param name="Cycle">The Open cycle, or null: no lot can be recorded.</param>
/// <param name="AverageKgPerCage">The current setting, to work out the estimated kg on the form.</param>
/// <param name="CanRecordHarvest">Show Record harvest: at least one lane is Growing or Being harvested, and the caller may record.</param>
/// <param name="Lanes">Lanes In Use, and Removed lanes that have records in the cycle shown, in number order.</param>
/// <param name="Lots">The lots of the Open cycle: lots for sale first, then the latest harvest first.</param>
public sealed record HarvestLotsScreen(
    Guid RaftId, string RaftCode, CycleListItem? Cycle, decimal AverageKgPerCage, bool CanRecordHarvest, IReadOnlyList<HarvestLaneView> Lanes, IReadOnlyList<HarvestLotListItem> Lots);

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
