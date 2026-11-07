using OMTS.Application.Features.Cultivation.Progress;
using OMTS.Application.Features.Harvest;
using OMTS.Domain.Entities.Harvest;

namespace OMTS.Application.Common.Interfaces.Persistence;

public interface IHarvestLotRepository
{
    /// <summary>The lot with its lanes, photos, cycle, raft and zone, tracked for editing.</summary>
    Task<HarvestLot?> GetForEditAsync(Guid lotId, CancellationToken cancellationToken);

    /// <summary>The same, read only.</summary>
    Task<HarvestLot?> GetAsync(Guid lotId, CancellationToken cancellationToken);

    /// <summary>The lots of a cycle with their lanes, read only, the latest harvest first.</summary>
    Task<IReadOnlyList<HarvestLot>> ListByCycleAsync(Guid cycleId, CancellationToken cancellationToken);

    /// <summary>Each lane of each lot of the cycle with its date: what the lane states are worked out from (BR-FARM-03).</summary>
    Task<IReadOnlyList<LaneHarvest>> LaneHarvestsAsync(Guid cycleId, CancellationToken cancellationToken);

    /// <summary>The lots of the cycle that include the lane (CYCLE-05.0 step 3), each with its lot, read only, the latest harvest first.</summary>
    Task<IReadOnlyList<HarvestLotLane>> ListOfLaneAsync(Guid cycleId, Guid laneId, CancellationToken cancellationToken);

    /// <summary>Each lane of each lot of the cycle with its lot and time: what a correction of one of the lots is checked against (BR-HARVEST-01).</summary>
    Task<IReadOnlyList<LotLaneFact>> LaneFactsAsync(Guid cycleId, CancellationToken cancellationToken);

    /// <summary>
    /// The lot codes ever given from <paramref name="baseCode"/>, the code itself and those with a suffix: the lots there are, and through their <c>HarvestLot.Created</c> audit rows,
    /// which commit with the lot and are never deleted, the lots removed since. So a removed code is not given again (PM default).
    /// </summary>
    Task<IReadOnlyList<string>> CodesFromAsync(string baseCode, CancellationToken cancellationToken);

    /// <summary>The lots for sale (Open or Reopened) with their raft and zone, read only, the latest harvest first. Null: of every zone.</summary>
    Task<IReadOnlyList<HarvestLot>> ListForSaleAsync(IReadOnlyCollection<Guid>? zoneIds, CancellationToken cancellationToken);

    /// <summary>The lots named on a sales invoice, whatever their status, with their raft and zone, tracked: the sale marks them changed or closes them (SALE-05).</summary>
    Task<IReadOnlyList<HarvestLot>> ListForInvoiceAsync(IReadOnlyCollection<Guid> lotIds, CancellationToken cancellationToken);

    /// <summary>
    /// The lots of the yield report (BR-HARVEST-07) with their raft, zone and cycle, read only: those harvested from <paramref name="from"/> to <paramref name="to"/> on rafts of the zones,
    /// or with <paramref name="wholeCycles"/> every lot of each cycle that has such a lot. Null zones: every zone.
    /// </summary>
    Task<IReadOnlyList<HarvestLot>> ListForYieldAsync(IReadOnlyCollection<Guid>? zoneIds, DateOnly from, DateOnly to, bool wholeCycles, CancellationToken cancellationToken);

    /// <summary>How many lots are for sale now, of one zone or of every zone (BR-FARM-10).</summary>
    Task<int> CountForSaleAsync(Guid? zoneId, CancellationToken cancellationToken);

    /// <summary>The date of the last harvest lot of the cycle, or null when it has none (BR-CYCLE-06).</summary>
    Task<DateOnly?> LastHarvestDateAsync(Guid cycleId, CancellationToken cancellationToken);

    /// <summary>The lots still for sale whose days count from <paramref name="lastDay"/> or earlier: the day reopened, else the harvest date (GFR-05).</summary>
    Task<int> CountForSaleSinceAtMostAsync(DateOnly lastDay, CancellationToken cancellationToken);

    /// <summary>The ids of the same lots, oldest first: what the daily close check looks at (GFR-05).</summary>
    Task<IReadOnlyList<Guid>> ListForSaleSinceAtMostAsync(DateOnly lastDay, CancellationToken cancellationToken);

    /// <summary>Adds the lot with the lanes and photos it holds.</summary>
    void Add(HarvestLot lot);

    /// <summary>Removes the lot with its lanes and photos: it is kept only in the audit log (BR-HARVEST-05).</summary>
    void Remove(HarvestLot lot);

    void AddLane(HarvestLotLane lane);

    void RemoveLane(HarvestLotLane lane);

    void AddPhoto(HarvestLotPhoto photo);

    void RemovePhoto(HarvestLotPhoto photo);
}
