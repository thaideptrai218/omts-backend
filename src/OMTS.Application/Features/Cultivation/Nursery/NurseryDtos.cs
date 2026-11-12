using OMTS.Application.Common.Interfaces.Persistence;
using OMTS.Application.Features.Cultivation.Seeding;

namespace OMTS.Application.Features.Cultivation.Nursery;

/// <summary>NURSERY-01. The photos (1 to 3) travel as multipart parts apart from these fields.</summary>
/// <param name="SeedLotId">The <c>id</c> of a lot of the nursery seed lot list: the receipt line. One lot per batch.</param>
/// <param name="Quantity">Pieces put into the nursery, above 0, not above what is on hand in the lot.</param>
/// <param name="StartDate">Not later than today and not before the lot was received; today when left out.</param>
/// <param name="OtherFilePart">The request carried a file part not named <c>photos</c>.</param>
public sealed record StartNurseryRequest(Guid? SeedLotId, decimal? Quantity, DateOnly? StartDate, string? Note, bool OtherFilePart = false);

/// <summary>
/// NURSERY-02 edit while Nursing: a field left out stays as it is; a note sent empty is cleared. The photos change only when <paramref name="ReplacePhotos"/> is true (the form sent
/// <c>photos</c> or <c>keepPhotoIds</c>): then the batch keeps exactly <paramref name="KeepPhotoIds"/> and adds the new parts, in all 1 to 3.
/// </summary>
public sealed record EditNurseryRequest(
    decimal? Quantity, DateOnly? StartDate, string? Note, string? RowVersion, bool ReplacePhotos = false, IReadOnlyList<Guid>? KeepPhotoIds = null, bool OtherFilePart = false);

/// <summary>NURSERY-02 cancel while Nursing: the reason is required and the pieces go back in the stock.</summary>
public sealed record CancelNurseryRequest(string? Reason, string? RowVersion);

/// <param name="RaftId">A raft of the zone of the batch.</param>
/// <param name="Quantity">The pieces of the survivors given to the raft, above 0.</param>
/// <param name="TargetYieldKg">Optional plan of the cycle of the raft, above 0. When the shares are changed after the completion, left out keeps the one the cycle has.</param>
public sealed record NurseryAllocationInput(Guid? RaftId, decimal? Quantity, decimal? TargetYieldKg = null);

/// <summary>NURSERY-03. The photos (1 to 3) travel as multipart parts apart from these fields.</summary>
/// <param name="Survivors">Pieces that survived, counted by eye (an estimate), from 0 to the pieces put in.</param>
/// <param name="CompletedOn">Not before the start date and not later than today; today when left out. It is the start date of the cycles created.</param>
/// <param name="Allocations">One entry per raft; the quantities add up to <paramref name="Survivors"/> exactly. Empty only when no piece survived.</param>
public sealed record CompleteNurseryRequest(
    decimal? Survivors, DateOnly? CompletedOn, IReadOnlyList<NurseryAllocationInput>? Allocations, string? RowVersion, bool OtherFilePart = false, bool AllocationsUnreadable = false);

/// <summary>
/// NURSERY-04: the survivors as they should be, old to new in the history. Not below what was seeded. When the new number differs from the shares, <paramref name="Allocations"/> says
/// the new shares (they add up to it); a batch with one raft needs none.
/// </summary>
public sealed record CorrectSurvivorsRequest(decimal? Survivors, string? Reason, IReadOnlyList<NurseryAllocationInput>? Allocations, string? RowVersion);

/// <summary>NURSERY-04 transfer: the shares of the survivors between the rafts (one, or many), the same total, before the seed is hung on the part that moves.</summary>
public sealed record ChangeNurseryAllocationsRequest(IReadOnlyList<NurseryAllocationInput>? Allocations, string? Reason, string? RowVersion);

/// <summary>A photo of a step, as the client reads it: <c>url</c> is a short-lived signed link.</summary>
public sealed record NurseryPhotoView(Guid Id, string Url);

/// <summary>The share of a raft in a Completed batch and its cycle (created at the completion).</summary>
/// <param name="Seeded">The seed hung so far in the cycle, from the nursery.</param>
/// <param name="Remaining">What can still be hung: <c>quantity</c> less <c>seeded</c>, never below 0.</param>
/// <param name="CycleStatus"><c>Open</c> or <c>Closed</c>; the share of a Closed cycle is frozen.</param>
/// <param name="CanChange">The share can be moved or removed by the transfer: its cycle is Open.</param>
public sealed record NurseryAllocationView(
    Guid Id, Guid RaftId, string RaftCode, Guid CycleId, DateOnly CycleStartDate, string CycleStatus, decimal Quantity, decimal Seeded, decimal Remaining, decimal? TargetYieldKg, bool CanChange);

/// <summary>One batch of the list (NURSERY-02).</summary>
/// <param name="DaysNursing">From the start date to the completion date (today while Nursing; the cancel day when Cancelled); both days count.</param>
/// <param name="LossPieces">Pieces put in less survivors: an estimate, as the survivors are counted by eye; null until the batch is Completed.</param>
/// <param name="LossPercent">The same as a percentage of the pieces put in, one decimal.</param>
/// <param name="Estimated">Always true when <c>lossPieces</c> is given.</param>
public sealed record NurseryBatchListItem(
    Guid Id, string Code, Guid ZoneId, string ZoneCode, string ZoneName, string SeedLotCode, string? ItemName, string Unit, string SupplierName, decimal QuantityIn, DateOnly StartDate,
    int DaysNursing, int StatusId, string Status, string StatusLabel, DateOnly? CompletedOn, decimal? Survivors, decimal? LossPieces, decimal? LossPercent, bool Estimated, int AllocationCount,
    string RowVersion);

/// <summary>A batch in full (NURSERY-02).</summary>
/// <param name="SeedLotOnHand">What the lot has on hand now (received, less returns and everything consumed), in <c>unit</c>.</param>
/// <param name="CanEdit">Nursing, and the caller may change it: edit, cancel.</param>
/// <param name="CanComplete">Nursing, and the caller may record the completion.</param>
/// <param name="CanCorrectSurvivors">Completed, and the caller may correct the survivors or move the shares.</param>
public sealed record NurseryBatchView(
    Guid Id, string Code, Guid ZoneId, string ZoneCode, string ZoneName, SeedLotRef SeedLot, decimal SeedLotOnHand, decimal QuantityIn, string Unit, DateOnly StartDate, string? Note,
    int DaysNursing, int StatusId, string Status, string StatusLabel, DateOnly? CompletedOn, decimal? Survivors, decimal? LossPieces, decimal? LossPercent, bool Estimated,
    Guid? IssueId, string? IssueCode, string? CancelReason, DateTimeOffset? CancelledAt, Guid? RecordedByAccountId, string? RecordedByName, DateTimeOffset CreatedAt,
    IReadOnlyList<NurseryPhotoView> StartPhotos, IReadOnlyList<NurseryPhotoView> CompletionPhotos, IReadOnlyList<NurseryAllocationView> Allocations,
    bool CanEdit, bool CanComplete, bool CanCorrectSurvivors, string RowVersion);

/// <param name="Message">MSG-GEN-05 "Nursery batch saved.".</param>
public sealed record NurseryBatchSaved(NurseryBatchView Batch, string Message, string MessageCode);

public sealed record NurseryListSummary(
    decimal QuantityIn, decimal NursingQuantityIn, decimal CompletedQuantityIn, decimal Survivors, decimal LossPieces, decimal? LossPercent, int NursingCount, int CompletedCount);

public sealed record NurseryBatchPage(IReadOnlyList<NurseryBatchListItem> Items, int Page, int PageSize, int Total, NurseryListSummary Summary);

/// <summary>The list query. The period is on the start date.</summary>
/// <param name="Status"><c>all</c> (the default), <c>nursing</c>, <c>completed</c> or <c>cancelled</c>.</param>
/// <param name="Search">Text in the code, the note, the receipt code, the supplier or the item name.</param>
/// <param name="SortBy"><c>code</c> (the default), <c>startDate</c>, <c>quantityIn</c> or <c>status</c>.</param>
/// <param name="SortDir"><c>asc</c> or <c>desc</c> (the default).</param>
public sealed record NurseryListQuery(
    Guid? ZoneId = null, string? Status = null, DateOnly? From = null, DateOnly? To = null, string? Search = null, string? SortBy = null, string? SortDir = null, int Page = 1, int PageSize = 20);

/// <summary>The seed lots a nursery can take (a lot with something on hand), for the form of NURSERY-01.</summary>
public sealed record NurserySeedLotList(Guid ZoneId, IReadOnlyList<SeedLotOption> Lots, string? Message, string? MessageCode);

/// <summary>A share of a Completed batch that still has seed to hang, for the seeding screen.</summary>
public sealed record AvailableNurseryAllocation(Guid AllocationId, Guid CycleId, Guid RaftId, string RaftCode, decimal Allocated, decimal Seeded, decimal Remaining);

public sealed record AvailableNurseryBatch(
    Guid Id, string Code, Guid ZoneId, string ZoneCode, string SeedLotCode, string? ItemName, string Unit, decimal QuantityIn, decimal Survivors, DateOnly? CompletedOn,
    decimal Remaining, IReadOnlyList<AvailableNurseryAllocation> Allocations);

public sealed record AvailableNurseryList(IReadOnlyList<AvailableNurseryBatch> Batches);

/// <summary>The nursery of a cycle, in the summary, the close preview and the yield report; the pieces are an estimate (counted by eye).</summary>
/// <param name="QuantityIn">For a cycle that shares its batch with other rafts: the pieces put in, in proportion to the share of the cycle.</param>
/// <param name="Survivors">The share of the cycle.</param>
public sealed record NurserySummary(Guid BatchId, string BatchCode, decimal QuantityIn, decimal Survivors, decimal LossPieces, decimal LossPercent, bool Estimated);
