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

/// <summary>
/// Totals of every batch the filters match, not only the page (a Cancelled batch counts in none of the quantities). "Completed" means done nursing: Completed and Closed batches, so the survivors and the
/// loss cover both; <paramref name="CloseLossPieces"/> is what the Closed batches wrote off.
/// </summary>
public sealed record NurseryListTotals(
    decimal QuantityIn, decimal NursingQuantityIn, decimal CompletedQuantityIn, decimal Survivors, int NursingCount, int CompletedCount, decimal CloseLossPieces = 0m, int ClosedCount = 0);

/// <summary>
/// What a cycle drawing from a nursery batch knows of it (v2): the batch, how much of it the cycle and the whole batch have seeded, and the close loss. The loss of the batch is shared between its
/// cycles by what each seeded.
/// </summary>
public sealed record NurseryCycleFacts(Guid BatchId, string BatchCode, decimal QuantityIn, decimal Survivors, decimal CloseLoss, decimal CycleSeeded, decimal BatchSeeded);

/// <summary>One cycle that takes seed from a batch (the "usage" of the batch): what it seeded and when it last did.</summary>
public sealed record NurseryUsageRow(Guid CycleId, DateOnly CycleStartDate, int CycleStatusId, Guid RaftId, string RaftCode, decimal Seeded, DateOnly LastSeedingOn);

public interface INurseryBatchRepository
{
    /// <summary>The batch with its zone, seed lot (item, receipt, supplier), issue and photos, tracked for editing.</summary>
    Task<NurseryBatch?> GetForEditAsync(Guid batchId, CancellationToken cancellationToken);

    /// <summary>The same, read only.</summary>
    Task<NurseryBatch?> GetAsync(Guid batchId, CancellationToken cancellationToken);

    /// <summary>A page of batches (zone, seed lot with item, receipt and supplier; no photos) and the totals of the whole selection.</summary>
    Task<(IReadOnlyList<NurseryBatch> Rows, int Total, NurseryListTotals Totals)> SearchAsync(NurseryBatchFilter filter, int page, int pageSize, CancellationToken cancellationToken);

    /// <summary>The Completed (ready: "đã ươm xong") batches of the zones (all when <paramref name="zoneIds"/> is null), read only, newest first.</summary>
    Task<IReadOnlyList<NurseryBatch>> CompletedAsync(Guid? zoneId, IReadOnlySet<Guid>? zoneIds, CancellationToken cancellationToken);

    /// <summary>The seed lot of the batch (receipt line with item, receipt and supplier), tracked: a seeding session of a cycle of the batch carries it.</summary>
    Task<PurchaseReceiptLine?> SeedLotOfAsync(Guid batchId, CancellationToken cancellationToken);

    /// <summary>The batch with its zone and its seed lot (item, receipt, supplier), TRACKED, no photos or issue: what a cycle or a seeding session needs to know of the pool; they mark the row changed so a parallel close, correction or seeding conflicts (MSG06).</summary>
    Task<NurseryBatch?> GetForPoolAsync(Guid batchId, CancellationToken cancellationToken);

    /// <summary>The batch facts of each of the cycles that draws from a nursery batch; a cycle started the old way is left out.</summary>
    Task<IReadOnlyDictionary<Guid, NurseryCycleFacts>> FactsByCycleAsync(IReadOnlyCollection<Guid> cycleIds, CancellationToken cancellationToken);

    /// <summary>The seed hung in each of the cycles (the total of its seeding sessions); a cycle with none is left out.</summary>
    Task<IReadOnlyDictionary<Guid, decimal>> SeededByCycleAsync(IReadOnlyCollection<Guid> cycleIds, CancellationToken cancellationToken);

    /// <summary>The pieces seeded from each batch over every cycle (the seeding records carrying the batch); a batch with none is left out. <paramref name="exceptRecordId"/> leaves one record out (the one being corrected).</summary>
    Task<IReadOnlyDictionary<Guid, decimal>> SeededByBatchAsync(IReadOnlyCollection<Guid> batchIds, Guid? exceptRecordId, CancellationToken cancellationToken);

    /// <summary>The cycles that seeded from the batch, the one with the most seed first.</summary>
    Task<IReadOnlyList<NurseryUsageRow>> UsageAsync(Guid batchId, CancellationToken cancellationToken);

    /// <summary>The completion photos of the batch in slot order, read only: shown as the plan photos of an old cycle that has none of its own.</summary>
    Task<IReadOnlyList<NurseryBatchPhoto>> CompletionPhotosAsync(Guid batchId, CancellationToken cancellationToken);

    void Add(NurseryBatch batch);

    void AddPhoto(NurseryBatchPhoto photo);

    void RemovePhoto(NurseryBatchPhoto photo);
}
