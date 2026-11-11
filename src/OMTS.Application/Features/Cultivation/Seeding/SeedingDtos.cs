using OMTS.Application.Features.Cultivation.Cycles;
using OMTS.Application.Features.Cultivation.Progress;

namespace OMTS.Application.Features.Cultivation.Seeding;

/// <summary>CYCLE-02.0: one seed lot and the lanes on which its seed was hung.</summary>
/// <param name="SeedLotId">The <c>id</c> of a lot of the seed lot list: the receipt line.</param>
public sealed record RecordSeedingRequest(DateOnly? SeedingDate, Guid? SeedLotId, IReadOnlyList<int>? LaneNumbers, decimal? Quantity);

/// <summary>CYCLE-02.2: the session as it should be, with the reason and the version read (NFR-09).</summary>
public sealed record CorrectSeedingRequest(DateOnly? SeedingDate, Guid? SeedLotId, IReadOnlyList<int>? LaneNumbers, decimal? Quantity, string? Reason, string? RowVersion);

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
    decimal QuantityReceived, decimal QuantitySeeded, bool BoughtForThisRaft);

/// <param name="Lots">Those bought for this raft first, then the others, the newest first.</param>
/// <param name="Message">MSG-CYCLE-03 when there is no lot to choose.</param>
/// <param name="MessageCode">The code of <paramref name="Message"/>.</param>
public sealed record SeedLotList(Guid RaftId, IReadOnlyList<SeedLotOption> Lots, string? Message, string? MessageCode);

/// <summary>A seeding session (CYCLE-02, CYCLE-05).</summary>
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
    string RowVersion);

/// <summary>Answer of a saved session.</summary>
/// <param name="OfferMarkComplete">No lane In Use is left Not seeded and the seeding is not yet marked complete (CYCLE-02.0 step 7).</param>
/// <param name="CycleRowVersion">The new version of the cycle, as every session moves it: send it to mark the seeding complete.</param>
/// <param name="Message">MSG-GEN-05 "Seeding session saved.".</param>
/// <param name="MessageCode">The code of <paramref name="Message"/>.</param>
public sealed record SeedingRecordSaved(SeedingRecordView Record, bool OfferMarkComplete, string CycleRowVersion, string Message, string MessageCode);

/// <summary>A lane on the Seeding screen.</summary>
/// <param name="CanSeed">The lane can be chosen for a new session now (BR-CYCLE-02).</param>
public sealed record SeedingLaneView(Guid Id, int LaneNumber, int StatusId, string Status, LaneState State, bool CanSeed);

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
    string CycleRowVersion);

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
public sealed record LaneProgressView(
    Guid RaftId, string RaftCode, Guid LaneId, int LaneNumber, int StatusId, string Status, LaneState State, CycleListItem? Cycle, IReadOnlyList<LaneSeedingSession> Sessions, IReadOnlyList<LaneHarvestLot> HarvestLots);

/// <summary>A seed lot hung on a lane, with its seeding dates, the earliest first (CYCLE-05.0 step 1).</summary>
public sealed record LaneSeedLot(Guid SeedLotId, string Code, string? ItemName, IReadOnlyList<DateOnly> Dates);

/// <summary>The seed lots of a seeded lane (CYCLE-05.0 step 1), by code.</summary>
public sealed record LaneSeedLots(int LaneNumber, IReadOnlyList<LaneSeedLot> SeedLots);
