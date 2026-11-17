using OMTS.Application.Features.Cultivation.Cycles;
using OMTS.Application.Features.Cultivation.Progress;

namespace OMTS.Application.Features.Cultivation.Seeding;

/// <summary>CYCLE-02.0: one seed lot and the lanes on which its seed was hung.</summary>
/// <param name="SeedLotId">The <c>id</c> of a lot of the seed lot list: the receipt line.</param>
/// <param name="Quantity">The pieces taken today, above 0: chosen freely, and for a cycle that draws from a nursery batch (v2, 13/11/2026) capped by what is left in the pool of the batch (409 NURSERY_POOL_EXCEEDED). The seed lot is then the lot of the batch: <paramref name="SeedLotId"/> is ignored.</param>
/// <param name="ConfirmOverPlan">The answer to the over-plan question (409 MSG-GEN-04 with <c>confirmFlag: "confirmOverPlan"</c>): the seed of the cycle goes above 110 % of its planned seed; needs <paramref name="OverPlanReason"/>.</param>
/// <param name="LanePercents">The share of each lane the session hangs (lane number to percent, owner decision of 12/11/2026); a lane left out takes 100. Null when none was sent.</param>
/// <param name="LanePercentsUnreadable"><c>lanePercents</c> was sent but is not a JSON object of numbers: refused on <c>LanePercent</c>.</param>
/// <param name="OtherFilePart">A file came in a part not named <c>photos</c>: refused, so no photo is dropped without a word.</param>
public sealed record RecordSeedingRequest(
    DateOnly? SeedingDate, Guid? SeedLotId, IReadOnlyList<int>? LaneNumbers, decimal? Quantity, bool ConfirmOverPlan = false, string? OverPlanReason = null,
    IReadOnlyDictionary<int, decimal>? LanePercents = null, bool LanePercentsUnreadable = false, bool OtherFilePart = false);

/// <summary>
/// CYCLE-02.2: the session as it should be, with the reason and the version read (NFR-09). <paramref name="ChangesPhotos"/> is true when the form sent <c>keepPhotoIds</c> or <c>photos</c>: the photos then become exactly the ones
/// kept (<paramref name="KeepPhotoIds"/>) and the new ones, 1 to 3; otherwise they stay. A lane left out of <paramref name="LanePercents"/> keeps its share.
/// </summary>
public sealed record CorrectSeedingRequest(
    DateOnly? SeedingDate, Guid? SeedLotId, IReadOnlyList<int>? LaneNumbers, decimal? Quantity, string? Reason, string? RowVersion, bool ConfirmOverPlan = false, string? OverPlanReason = null,
    IReadOnlyDictionary<int, decimal>? LanePercents = null, bool LanePercentsUnreadable = false, bool OtherFilePart = false, bool ChangesPhotos = false, IReadOnlyList<Guid>? KeepPhotoIds = null);

/// <summary>A photo of a session: <paramref name="Url"/> is a signed link that expires.</summary>
public sealed record SeedingPhotoView(Guid Id, int SortOrder, string Url);

/// <summary>The share of a lane a session hangs.</summary>
public sealed record SeedingLanePercentView(int LaneNumber, decimal Percent);

/// <summary>CYCLE-02.2 remove.</summary>
public sealed record RemoveSeedingRequest(string? Reason, string? RowVersion);

/// <summary>CYCLE-02.1.</summary>
/// <param name="RowVersion">The version of the cycle.</param>
/// <param name="ConfirmEmpty">The Zone Manager confirms finishing a seeding with no seeded lane (409 with <c>confirmFlag: "confirmEmpty"</c> otherwise).</param>
public sealed record MarkSeedingCompleteRequest(string? RowVersion, bool ConfirmEmpty = false);

/// <summary>A seed lot on a session: traced to its receipt and supplier (BR-FARM-03). It holds no price.</summary>
public sealed record SeedLotRef(
    Guid Id, string Code, Guid ReceiptId, string ReceiptCode, DateOnly ReceivedDate, Guid? ItemId, string? ItemCode, string? ItemName, string Unit, Guid SupplierId, string SupplierName);

/// <summary>A seed lot to choose from (CYCLE-02.0 step 3).</summary>
/// <param name="Id">The receipt line: send it as <c>seedLotId</c>.</param>
/// <param name="Code">The receipt code and the line number, e.g. PN000123-1 (GBR-04).</param>
/// <param name="QuantitySeeded">Shown only: seeding does not compare with what is left in the lot (GBR-04).</param>
/// <param name="BoughtForThisRaft">The receipt line names this raft.</param>
public sealed record SeedLotOption(
    Guid Id, string Code, Guid ReceiptId, string ReceiptCode, DateOnly ReceivedDate, Guid? ItemId, string? ItemCode, string? ItemName, string Unit, Guid SupplierId, string SupplierName,
    decimal QuantityReceived, decimal QuantitySeeded, bool BoughtForThisRaft, decimal QuantityReturned = 0m, decimal QuantityConsumed = 0m)
{
    /// <summary>What is left to use: received, less returns and everything consumed (seeding, manual issues, disposals). Seeding is refused above it (MSG-INPUT-01) unless the cooperative allows a negative stock.</summary>
    public decimal OnHand => QuantityReceived - QuantityReturned - QuantityConsumed;
}

/// <param name="Lots">Those bought for this raft first, then the others, the newest first.</param>
/// <param name="Message">MSG-CYCLE-03 when there is no lot to choose.</param>
/// <param name="MessageCode">The code of <paramref name="Message"/>.</param>
public sealed record SeedLotList(Guid RaftId, IReadOnlyList<SeedLotOption> Lots, string? Message, string? MessageCode);

/// <summary>A seeding session (CYCLE-02, CYCLE-05). <c>issueId</c> / <c>issueCode</c> name the stock issue (Xuất dùng) it posted; null on a session recorded before stock was checked.</summary>
/// <param name="LaneNumbers">In number order.</param>
/// <param name="CanCorrect">The caller may correct or remove it now: the cycle is Open on a raft In Use, no lane of it is in a harvest lot, and the caller recorded it or is the Owner (BR-CYCLE-02).</param>
public sealed record SeedingRecordView(
    Guid Id,
    Guid CycleId,
    Guid RaftId,
    string RaftCode,
    DateOnly SeedingDate,
    SeedLotRef SeedLot,
    decimal Quantity,
    IReadOnlyList<int> LaneNumbers,
    Guid RecordedByAccountId,
    string? RecordedByName,
    DateTimeOffset RecordedAt,
    bool CanCorrect,
    string RowVersion,
    Guid? IssueId = null,
    string? IssueCode = null,
    string? OverPlanReason = null,
    IReadOnlyList<SeedingPhotoView>? Photos = null,
    IReadOnlyList<SeedingLanePercentView>? LanePercents = null);

/// <summary>Answer of a saved session.</summary>
/// <param name="OfferMarkComplete">No lane In Use is left Not seeded and the seeding is not yet marked complete (CYCLE-02.0 step 7).</param>
/// <param name="CycleRowVersion">The new version of the cycle, as every session moves it: send it to mark the seeding complete.</param>
/// <param name="Message">MSG-GEN-05 "Seeding session saved.".</param>
/// <param name="MessageCode">The code of <paramref name="Message"/>.</param>
public sealed record SeedingRecordSaved(SeedingRecordView Record, bool OfferMarkComplete, string CycleRowVersion, string Message, string MessageCode);

/// <summary>A lane on the Seeding screen.</summary>
/// <param name="CanSeed">The lane can be chosen for a new session now (BR-CYCLE-02): not damaged and not out of service (decision of 12/11/2026).</param>
/// <param name="ReasonCode"><c>LaneDamaged</c> or <c>LaneOutOfService</c> when a flag of the lane is why it cannot be seeded; null otherwise.</param>
/// <param name="SeededPercent">The share of the lane hung in the cycle (0 to 100); a session adds at most what is left, <c>100 - seededPercent</c> (owner decision of 12/11/2026).</param>
/// <param name="HarvestedPercent">The share harvested; null when nobody entered it. <paramref name="LastModifiedByName"/>, <paramref name="LastModifiedAt"/> and <paramref name="ProgressHistory"/> are the manual changes of the percentages.</param>
public sealed record SeedingLaneView(
    Guid Id, int LaneNumber, int StatusId, string Status, LaneState State, bool CanSeed, bool IsDamaged = false, bool IsOutOfService = false, string? ReasonCode = null,
    decimal SeededPercent = 0m, decimal? HarvestedPercent = null, string? LastModifiedByName = null, DateTimeOffset? LastModifiedAt = null,
    IReadOnlyList<LaneProgressChangeView>? ProgressHistory = null, decimal RemainingPercent = 100m);

/// <summary>The total quantity of the sessions of a cycle from one seed lot (CYCLE-05.2).</summary>
public sealed record SeedLotTotal(Guid SeedLotId, string Code, string? ItemName, string Unit, decimal Quantity);

/// <summary>The Seeding screen (SCR-CYCLE-02) and CYCLE-05.2: the lanes today (a closed cycle: on its close date), the sessions and the total per seed lot.</summary>
/// <param name="LaneCount">The lanes In Use: Removed lanes are not counted (BR-CYCLE-02).</param>
/// <param name="SeededCount">The lanes In Use that have seed.</param>
/// <param name="NotSeededLanes">The numbers of the lanes In Use that are Not seeded.</param>
/// <param name="CanRecord">Show Add seeding session.</param>
/// <param name="CanMarkComplete">Show Mark seeding complete: only while the seeding is not yet marked complete.</param>
/// <param name="Lanes">Lanes In Use, and Removed lanes that have records in the cycle, in number order.</param>
/// <param name="Records">The latest date first.</param>
/// <param name="CycleRowVersion">Send it to mark the seeding complete.</param>
/// <param name="NurseryBatchId">The nursery batch the cycle draws from (v2, 13/11/2026): a session then needs no seed lot, and takes any quantity up to <paramref name="NurseryRemaining"/>, what is left in the pool of the batch for every cycle; <paramref name="NurserySeeded"/> is what this cycle took. Null otherwise, with the other nursery fields.</param>
/// <param name="SeedLot">The seed lot of the batch (read only); on a cycle started the old way, its main seeding lot (the one with the most seed).</param>
/// <param name="SeedItemName">The seed the cycle farms: the item of the batch's lot, or on a cycle started the old way the item of its main seeding lot; null while it has no seed.</param>
/// <param name="SeedItemNames">A cycle started the old way and seeded from several seed items: the distinct names, the one with most seed first; null otherwise.</param>
/// <param name="SeedableLaneNumbers">The lanes the next session can choose: they can be seeded and are below 100 % (a lane at 100 % is hidden from the choice, still drawn on the overview).</param>
public sealed record SeedingList(
    Guid CycleId,
    Guid RaftId,
    string RaftCode,
    DateOnly? SeedingCompletedOn,
    int LaneCount,
    int SeededCount,
    IReadOnlyList<int> NotSeededLanes,
    bool CanRecord,
    bool CanMarkComplete,
    IReadOnlyList<SeedingLaneView> Lanes,
    IReadOnlyList<SeedingRecordView> Records,
    IReadOnlyList<SeedLotTotal> Lots,
    string CycleRowVersion,
    decimal? PlannedSeedQuantity = null,
    decimal SeededQuantity = 0m,
    decimal? Percent = null,
    Guid? NurseryBatchId = null,
    string? NurseryCode = null,
    decimal? NurserySeeded = null,
    decimal? NurseryRemaining = null,
    SeedLotRef? SeedLot = null,
    IReadOnlyList<int>? SeedableLaneNumbers = null,
    string? SeedItemName = null,
    IReadOnlyList<string>? SeedItemNames = null);

/// <summary>What to ask before marking the seeding complete (CYCLE-02.1 step 2).</summary>
/// <param name="Message">MSG-CYCLE-01, listing the lanes that are still Not seeded.</param>
/// <param name="MessageCode">The code of <paramref name="Message"/>.</param>
public sealed record SeedingCompletionPreview(
    Guid CycleId, string RaftCode, int LaneCount, int SeededCount, IReadOnlyList<int> NotSeededLanes, string Message, string MessageCode, string CycleRowVersion);

/// <summary>Answer of CYCLE-02.1: the cycle holds today as the date the seeding was marked complete.</summary>
public sealed record SeedingCompleted(CycleView Cycle, int LaneCount, int SeededCount, IReadOnlyList<int> NotSeededLanes);

/// <summary>A session that includes a lane (CYCLE-05.0 step 3).</summary>
/// <param name="Quantity">The total of the session: no quantity per lane is ever worked out.</param>
public sealed record LaneSeedingSession(Guid Id, DateOnly SeedingDate, SeedLotRef SeedLot, decimal Quantity);

/// <summary>A harvest lot that includes a lane (CYCLE-05.0 step 3). It holds no kg: none is worked out per lane (AC-03).</summary>
/// <param name="IsFinished">This lot marked the lane finished.</param>
public sealed record LaneHarvestLot(Guid Id, string LotCode, DateOnly HarvestDate, bool IsFinished, int StatusId, string Status);

/// <summary>One lane in the cycle shown (CYCLE-05.0 step 3).</summary>
/// <param name="Cycle">The cycle shown, or null: the raft has no cycle yet.</param>
/// <param name="State">The state today, or on the close date of a closed cycle.</param>
/// <param name="Sessions">The latest date first.</param>
/// <param name="HarvestLots">The latest harvest first.</param>
/// <param name="SeededPercent">The percentages of the lane in the cycle shown and their manual changes, as on <see cref="SeedingLaneView"/>.</param>
public sealed record LaneProgressView(
    Guid RaftId, string RaftCode, Guid LaneId, int LaneNumber, int StatusId, string Status, LaneState State, CycleListItem? Cycle, IReadOnlyList<LaneSeedingSession> Sessions, IReadOnlyList<LaneHarvestLot> HarvestLots,
    decimal SeededPercent = 0m, decimal? HarvestedPercent = null, string? LastModifiedByName = null, DateTimeOffset? LastModifiedAt = null,
    IReadOnlyList<LaneProgressChangeView>? ProgressHistory = null);

/// <summary>A seed lot hung on a lane, with its seeding dates, the earliest first (CYCLE-05.0 step 1).</summary>
public sealed record LaneSeedLot(Guid SeedLotId, string Code, string? ItemName, IReadOnlyList<DateOnly> Dates);

/// <summary>The seed lots of a seeded lane (CYCLE-05.0 step 1), by code.</summary>
public sealed record LaneSeedLots(int LaneNumber, IReadOnlyList<LaneSeedLot> SeedLots);
