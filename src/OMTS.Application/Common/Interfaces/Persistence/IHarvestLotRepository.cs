using OMTS.Application.Features.Cultivation.Progress;
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

    /// <summary>The lot codes built from <paramref name="baseCode"/>: the code itself and those with a suffix.</summary>
    Task<IReadOnlyList<string>> CodesFromAsync(string baseCode, CancellationToken cancellationToken);

    /// <summary>The date of the last harvest lot of the cycle, or null when it has none (BR-CYCLE-06).</summary>
    Task<DateOnly?> LastHarvestDateAsync(Guid cycleId, CancellationToken cancellationToken);

    /// <summary>The lots still for sale whose days count from <paramref name="lastDay"/> or earlier: the day reopened, else the harvest date (GFR-05).</summary>
    Task<int> CountForSaleSinceAtMostAsync(DateOnly lastDay, CancellationToken cancellationToken);

    /// <summary>Adds the lot with the lanes and photos it holds.</summary>
    void Add(HarvestLot lot);

    /// <summary>
    /// Forgets every row this request read or added, so a new attempt after a lost race on the lot code reads the database again and writes no row of the failed
    /// one. A failed save has already dropped its queued pushes.
    /// </summary>
    void ForgetTrackedChanges();
}
