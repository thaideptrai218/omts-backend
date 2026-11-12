using OMTS.Domain.Entities.Cultivation;
using OMTS.Domain.Entities.Purchasing;

namespace OMTS.Application.Common.Interfaces.Persistence;

/// <summary>Nursery list filters; null means "any". A caller who sees only some zones passes them in <see cref="ZoneIds"/>. The period is on the start date.</summary>
public sealed record NurseryBatchFilter(
    Guid? ZoneId, IReadOnlySet<Guid>? ZoneIds, IReadOnlyList<int>? StatusIds, DateOnly? From, DateOnly? To, string? Search, NurseryBatchSort Sort, bool Descending);

public enum NurseryBatchSort
{
    Code,
    StartDate,
    QuantityIn,
    Status,
}

/// <summary>Totals of every batch the filters match, not only the page (a Cancelled batch counts in none of the quantities).</summary>
public sealed record NurseryListTotals(decimal QuantityIn, decimal NursingQuantityIn, decimal CompletedQuantityIn, decimal Survivors, int NursingCount, int CompletedCount);

/// <summary>What a cycle created from a nursery batch knows of it: the batch, and the seed hung so far against the share it holds.</summary>
public sealed record NurseryCycleFacts(Guid BatchId, string BatchCode, decimal QuantityIn, decimal Survivors, decimal Allocated, decimal Seeded);

public interface INurseryBatchRepository
{
    /// <summary>The batch with its zone, seed lot (item, receipt, supplier), issue, allocations (raft, cycle) and photos, tracked for editing.</summary>
    Task<NurseryBatch?> GetForEditAsync(Guid batchId, CancellationToken cancellationToken);

    /// <summary>The same, read only.</summary>
    Task<NurseryBatch?> GetAsync(Guid batchId, CancellationToken cancellationToken);

    /// <summary>A page of batches (zone, seed lot with item, receipt and supplier, allocations; no photos) and the totals of the whole selection.</summary>
    Task<(IReadOnlyList<NurseryBatch> Rows, int Total, NurseryListTotals Totals)> SearchAsync(NurseryBatchFilter filter, int page, int pageSize, CancellationToken cancellationToken);

    /// <summary>The Completed batches of the zones (all when <paramref name="zoneIds"/> is null) with their allocations, cycles and rafts, read only, newest first.</summary>
    Task<IReadOnlyList<NurseryBatch>> CompletedAsync(Guid? zoneId, IReadOnlySet<Guid>? zoneIds, CancellationToken cancellationToken);

    /// <summary>The seed lot of the batch (receipt line with item, receipt and supplier), tracked: a seeding session from the batch's cycle carries it.</summary>
    Task<PurchaseReceiptLine?> SeedLotOfAsync(Guid batchId, CancellationToken cancellationToken);

    /// <summary>The batch facts of each of the cycles that was created from a nursery batch; a cycle started the old way is left out.</summary>
    Task<IReadOnlyDictionary<Guid, NurseryCycleFacts>> FactsByCycleAsync(IReadOnlyCollection<Guid> cycleIds, CancellationToken cancellationToken);

    /// <summary>The seed hung in each of the cycles (the total of its seeding sessions); a cycle with none is left out.</summary>
    Task<IReadOnlyDictionary<Guid, decimal>> SeededByCycleAsync(IReadOnlyCollection<Guid> cycleIds, CancellationToken cancellationToken);

    /// <summary>The completion photos of the batch in slot order, read only: the plan photos of the cycles created from it (decision of 12/11/2026).</summary>
    Task<IReadOnlyList<NurseryBatchPhoto>> CompletionPhotosAsync(Guid batchId, CancellationToken cancellationToken);

    void Add(NurseryBatch batch);

    /// <summary>New rows under a tracked batch are added explicitly: their keys are already set.</summary>
    void AddAllocation(NurseryBatchAllocation allocation);

    void RemoveAllocation(NurseryBatchAllocation allocation);

    void AddPhoto(NurseryBatchPhoto photo);

    void RemovePhoto(NurseryBatchPhoto photo);
}
