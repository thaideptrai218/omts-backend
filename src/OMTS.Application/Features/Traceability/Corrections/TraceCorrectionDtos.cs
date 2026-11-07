using OMTS.Application.Features.Cultivation.CultivationLogs;
using OMTS.Application.Features.Cultivation.Cycles;
using OMTS.Application.Features.Cultivation.Seeding;
using OMTS.Application.Features.Harvest;
using OMTS.Domain.Enums;

namespace OMTS.Application.Features.Traceability.Corrections;

/// <summary>TRACE-03.0 step 4: the seeding session as it should be, with the reason, the version read (NFR-09) and the Zone Manager's answer to MSG-TRACE-04.</summary>
/// <param name="OwnerApproved">
/// A Zone Manager confirms that the Cooperative Owner agreed in person that the data is wrong (BR-TRACE-04); without it the save answers 409 MSG-TRACE-04 with
/// <c>confirmFlag: "ownerApproved"</c> and changes nothing. The Owner never needs it. A preview ignores it and the version.
/// </param>
public sealed record TraceSeedingCorrectionRequest(
    DateOnly? SeedingDate, Guid? SeedLotId, IReadOnlyList<int>? LaneNumbers, decimal? Quantity, string? Reason, string? RowVersion = null, bool OwnerApproved = false);

/// <summary>TRACE-03.2 preview: the harvest data as it should be. The harvest date and time are never sent: they never change (BR-TRACE-04).</summary>
/// <param name="KeepPhotoKeys">The photos that stay; null keeps them all. New photos are not previewed: they come with the save.</param>
public sealed record TraceHarvestLotPreviewRequest(
    IReadOnlyList<int>? LaneNumbers, IReadOnlyList<int>? FinishedLaneNumbers, int? CageCount, decimal? EstimatedKg, IReadOnlyList<string?>? KeepPhotoKeys, string? Reason);

/// <summary>A harvest lot whose origin information a correction changes, with its origin state, which the correction never changes (BR-TRACE-05).</summary>
public sealed record TraceAffectedLot(Guid Id, string LotCode, HarvestLotOriginState OriginState);

/// <summary>What TRACE-03 steps 5 and 6 show before the save. Changes nothing.</summary>
/// <param name="RecordType">SeedingRecord, CultivationLog or HarvestLot.</param>
/// <param name="Before">The record as it is, with the values the audit log keeps.</param>
/// <param name="After">The record as the correction would leave it; null for a cultivation log, whose new photos come only with the save.</param>
/// <param name="ChangedFields">The fields of <paramref name="Before"/> the correction changes; empty when it changes nothing.</param>
/// <param name="AffectedLots">The harvest lots whose origin information the correction changes; never one for a cultivation log (BR-TRACE-05).</param>
/// <param name="AffectedPublishedLotCodes">Those of them that are Published: their provenance pages show the correction at once (BR-TRACE-05).</param>
/// <param name="NeedsOwnerApproval">The caller is a Zone Manager: the save must send <c>ownerApproved: true</c> after asking <paramref name="ConfirmMessage"/>.</param>
/// <param name="ConfirmMessage">MSG-TRACE-04 for a Zone Manager; MSG-GEN-04 for the Cooperative Owner, who only confirms.</param>
/// <param name="RowVersion">The version of the record now: send it with the save (a cultivation log has none to check, NFR-09).</param>
public sealed record TraceCorrectionPreview(
    string RecordType,
    Guid RecordId,
    object Before,
    object? After,
    IReadOnlyList<string> ChangedFields,
    IReadOnlyList<TraceAffectedLot> AffectedLots,
    IReadOnlyList<string> AffectedPublishedLotCodes,
    bool NeedsOwnerApproval,
    string ConfirmMessage,
    string ConfirmMessageCode,
    string RowVersion);

/// <summary>Answer of a corrected seeding session (TRACE-03.0 step 8).</summary>
/// <param name="AffectedPublishedLotCodes">The Published lots whose provenance pages now show the corrected data; the audit row names them too.</param>
/// <param name="Message">MSG-GEN-05 "Seeding session saved.".</param>
public sealed record TraceSeedingCorrected(SeedingRecordView Record, IReadOnlyList<string> AffectedPublishedLotCodes, string Message, string MessageCode);

/// <summary>Answer of corrected harvest data (TRACE-03.2).</summary>
/// <param name="AffectedPublishedLotCodes">The lot itself when it is Published and the correction changed it.</param>
/// <param name="Message">MSG-GEN-05 "Harvest lot saved.".</param>
public sealed record TraceHarvestLotCorrected(HarvestLotView Lot, IReadOnlyList<string> AffectedPublishedLotCodes, string Message, string MessageCode);

/// <summary>SCR-TRACE-02 for a raft (TRACE-03.0 step 1): its cycles, Open and Closed, the newest first.</summary>
public sealed record TraceCorrectionCycles(Guid RaftId, string RaftCode, IReadOnlyList<CycleListItem> Cycles, bool NeedsOwnerApproval);

/// <summary>A seeding session that can be corrected only here.</summary>
/// <param name="LotCodes">The harvest lots of the cycle that hold a lane of the session, the earliest harvest first.</param>
public sealed record TraceSeedingItem(Guid Id, DateOnly SeedingDate, SeedLotRef SeedLot, decimal Quantity, IReadOnlyList<int> LaneNumbers, IReadOnlyList<string> LotCodes, string RowVersion);

/// <summary>A harvest lot of a Closed cycle; its photos are on <c>GET api/harvest-lots/{id}</c>.</summary>
public sealed record TraceHarvestLotItem(
    Guid Id,
    string LotCode,
    DateTimeOffset HarvestedAt,
    DateOnly HarvestDate,
    IReadOnlyList<int> LaneNumbers,
    IReadOnlyList<int> FinishedLaneNumbers,
    int CageCount,
    decimal KgPerCage,
    decimal EstimatedKg,
    int StatusId,
    string Status,
    HarvestLotOriginState OriginState,
    string RowVersion);

/// <summary>
/// SCR-TRACE-02 for a cycle (TRACE-03.0 step 3): the records the cultivation and harvest screens no longer allow the Zone Manager to change (BR-TRACE-04). An Open cycle lists only its
/// seeding sessions with a lane in a harvest lot; a Closed one also lists every session, log and lot.
/// </summary>
/// <param name="Message">MSG-GEN-10 when there is no such record (TRACE-03.0.E1), else null.</param>
public sealed record TraceCorrectionRecords(
    Guid CycleId,
    Guid RaftId,
    string RaftCode,
    int CycleStatusId,
    string CycleStatus,
    DateOnly StartDate,
    DateOnly? CloseDate,
    IReadOnlyList<TraceSeedingItem> SeedingRecords,
    IReadOnlyList<CultivationLogView> CultivationLogs,
    IReadOnlyList<TraceHarvestLotItem> HarvestLots,
    bool NeedsOwnerApproval,
    string? Message,
    string? MessageCode);
