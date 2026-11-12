using OMTS.Domain.Enums;

namespace OMTS.Application.Features.Harvest;

/// <summary>HARVEST-01.1: the lot as it should be, with new photos beside it. The harvest date and time, the lot code and the kg per cage are not sent: they never change here.</summary>
/// <param name="LaneNumbers">The lanes of the lot, each given once; null when a value could not be read.</param>
/// <param name="FinishedLaneNumbers">Those of them with no oysters left to harvest; none is fine; null when a value could not be read.</param>
/// <param name="EstimatedKg">Without it the estimate stays, or when the cages changed it is worked out with the kg per cage of the lot (BR-HARVEST-02).</param>
/// <param name="KeepPhotoKeys">The photos that stay; a photo left out is removed.</param>
/// <param name="Reason">Kept in the audit log.</param>
/// <param name="RowVersion">The version of the lot the form was read at (NFR-09).</param>
/// <param name="EstimatedKgIsUnreadable">A value was sent that is not a number, so it does not mean "leave it".</param>
/// <param name="OtherFilePart">A file came in a part not named <c>photos</c>: refused after the access check, so no photo is dropped without a word.</param>
public sealed record CorrectHarvestLotRequest(
    IReadOnlyList<int>? LaneNumbers,
    IReadOnlyList<int>? FinishedLaneNumbers,
    decimal? CageCount,
    decimal? EstimatedKg,
    IReadOnlyList<string?>? KeepPhotoKeys,
    string? Reason,
    string? RowVersion,
    bool EstimatedKgIsUnreadable = false,
    bool OtherFilePart = false,
    bool ConfirmOverPlan = false,
    string? OverPlanReason = null,
    IReadOnlyList<Guid>? Crew = null,
    bool CrewUnreadable = false,
    IReadOnlyDictionary<int, decimal>? LanePercents = null,
    bool LanePercentsUnreadable = false);

/// <summary>HARVEST-01.1 step 3; the client has asked with MSG-GEN-04.</summary>
public sealed record RemoveHarvestLotRequest(string? Reason, string? RowVersion);

/// <summary>A lot that can be chosen on a sales invoice (SALE-05 step 1, SCR-SALE-04). The remaining kg is for information only and limits no sale (BR-HARVEST-02).</summary>
/// <param name="OriginState">For the warning of MSG-SALE-09 when it is Withdrawn.</param>
/// <param name="WithdrawnAt">The withdrawal time, only while the lot is Withdrawn; the reason stays internal (BR-TRACE-06).</param>
public sealed record HarvestLotForSale(
    Guid Id,
    string LotCode,
    Guid RaftId,
    string RaftCode,
    Guid ZoneId,
    string ZoneCode,
    string ZoneName,
    DateOnly HarvestDate,
    decimal RemainingKg,
    int StatusId,
    string Status,
    HarvestLotOriginState OriginState,
    DateTimeOffset? WithdrawnAt);

/// <summary>The lots for sale in the zones of the caller.</summary>
/// <param name="Message">MSG-GEN-10 when there is none, else null.</param>
/// <param name="MessageCode">The code of <paramref name="Message"/>.</param>
public sealed record HarvestLotsForSale(IReadOnlyList<HarvestLotForSale> Lots, string? Message, string? MessageCode);

/// <summary>The open harvest lots of the operations dashboard (FARM-06, BR-FARM-10): worked out when asked, never stored.</summary>
/// <param name="ZoneId">The zone chosen, or null for every zone.</param>
public sealed record HarvestLotForSaleCount(Guid? ZoneId, int Count);
