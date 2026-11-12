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

/// <summary>NURSERY-03 (v2, 13/11/2026). The photos (1 to 3) travel as multipart parts apart from these fields. No shares and no cycles: the batch becomes a pool.</summary>
/// <param name="Survivors">Pieces that survived, counted by eye (an estimate), from 0 to the pieces put in.</param>
/// <param name="CompletedOn">Not before the start date and not later than today; today when left out.</param>
public sealed record CompleteNurseryRequest(decimal? Survivors, DateOnly? CompletedOn, string? RowVersion, bool OtherFilePart = false);

/// <summary>NURSERY-04 (v2): the survivors as they should be, old to new in the history, with a reason. Not below what was seeded.</summary>
public sealed record CorrectSurvivorsRequest(decimal? Survivors, string? Reason, string? RowVersion);

/// <summary>NURSERY-05 (v2): close a Completed batch; the reason is optional. The pieces left in the pool are written off as a loss estimate.</summary>
public sealed record CloseNurseryRequest(string? Reason, string? RowVersion);

/// <summary>A photo of a step, as the client reads it: <c>url</c> is a short-lived signed link.</summary>
public sealed record NurseryPhotoView(Guid Id, string Url);

/// <summary>A cycle that takes seed from the batch: what it seeded, and when it seeded last.</summary>
public sealed record NurseryUsageView(Guid CycleId, DateOnly CycleStartDate, string CycleStatus, Guid RaftId, string RaftCode, decimal Seeded, DateOnly LastSeedingOn);

/// <summary>One batch of the list (NURSERY-02).</summary>
/// <param name="DaysNursing">From the start date to the completion date (today while Nursing; the cancel day when Cancelled); both days count.</param>
/// <param name="Seeded">The pieces seeded from the batch over every cycle; null while Nursing and when Cancelled.</param>
/// <param name="Remaining">Survivors less seeded while Completed, 0 once Closed; null while Nursing and when Cancelled.</param>
/// <param name="LossPieces">Pieces put in less survivors, plus what a Closed batch wrote off: an estimate, as the survivors are counted by eye; null until the batch is done nursing.</param>
/// <param name="LossPercent">The same as a percentage of the pieces put in, one decimal.</param>
/// <param name="Estimated">Always true when <c>lossPieces</c> is given.</param>
public sealed record NurseryBatchListItem(
    Guid Id, string Code, Guid ZoneId, string ZoneCode, string ZoneName, string SeedLotCode, string? ItemName, string Unit, string SupplierName, decimal QuantityIn, DateOnly StartDate,
    int DaysNursing, int StatusId, string Status, string StatusLabel, DateOnly? CompletedOn, decimal? Survivors, decimal? LossPieces, decimal? LossPercent, bool Estimated,
    string RowVersion, decimal? Seeded = null, decimal? Remaining = null, DateOnly? ClosedOn = null, decimal? CloseLossPieces = null);

/// <summary>A batch in full (NURSERY-02).</summary>
/// <param name="SeedLotOnHand">What the lot has on hand now (received, less returns and everything consumed), in <c>unit</c>.</param>
/// <param name="CanEdit">Nursing, and the caller may change it: edit, cancel.</param>
/// <param name="CanComplete">Nursing, and the caller may record the completion.</param>
/// <param name="CanCorrectSurvivors">Completed, and the caller may correct the survivors.</param>
/// <param name="CanClose">Completed with pieces left in the pool, and the caller may close it.</param>
/// <param name="Usage">The cycles that seeded from the batch, the one with the most first.</param>
public sealed record NurseryBatchView(
    Guid Id, string Code, Guid ZoneId, string ZoneCode, string ZoneName, SeedLotRef SeedLot, decimal SeedLotOnHand, decimal QuantityIn, string Unit, DateOnly StartDate, string? Note,
    int DaysNursing, int StatusId, string Status, string StatusLabel, DateOnly? CompletedOn, decimal? Survivors, decimal? LossPieces, decimal? LossPercent, bool Estimated,
    Guid? IssueId, string? IssueCode, string? CancelReason, DateTimeOffset? CancelledAt, Guid? RecordedByAccountId, string? RecordedByName, DateTimeOffset CreatedAt,
    IReadOnlyList<NurseryPhotoView> StartPhotos, IReadOnlyList<NurseryPhotoView> CompletionPhotos,
    bool CanEdit, bool CanComplete, bool CanCorrectSurvivors, string RowVersion,
    decimal? Seeded = null, decimal? Remaining = null, DateOnly? ClosedOn = null, decimal? CloseLossPieces = null, string? CloseReason = null, bool CanClose = false,
    IReadOnlyList<NurseryUsageView>? Usage = null);

/// <param name="Message">MSG-GEN-05 "Nursery batch saved.".</param>
public sealed record NurseryBatchSaved(NurseryBatchView Batch, string Message, string MessageCode);

public sealed record NurseryListSummary(
    decimal QuantityIn, decimal NursingQuantityIn, decimal CompletedQuantityIn, decimal Survivors, decimal LossPieces, decimal? LossPercent, int NursingCount, int CompletedCount, int ClosedCount = 0);

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

/// <summary>A batch ready to be chosen when a cycle is started (v2): Completed with pieces left in the pool.</summary>
public sealed record AvailableNurseryBatch(
    Guid Id, string Code, Guid ZoneId, string ZoneCode, string SeedLotCode, string? ItemName, string Unit, decimal QuantityIn, decimal Survivors, decimal Seeded, decimal Remaining, DateOnly? CompletedOn);

public sealed record AvailableNurseryList(IReadOnlyList<AvailableNurseryBatch> Batches);

/// <summary>
/// The nursery of a cycle, in the summary, the close preview and the yield report; the pieces are an estimate (counted by eye). A batch can feed several cycles, so its figures are shared between them by
/// what each seeded (<c>share</c> = seeded by the cycle / seeded by the batch; 0 when nothing was seeded).
/// </summary>
/// <param name="QuantityIn">The pieces put in of the whole batch times the share of the cycle.</param>
/// <param name="Survivors">The survivors of the whole batch times the share of the cycle.</param>
/// <param name="LossPieces">The loss of the whole batch (put in less survivors, plus what a close wrote off) times the share of the cycle.</param>
/// <param name="LossPercent">The loss of the whole batch as a percentage of the pieces put in.</param>
public sealed record NurserySummary(Guid BatchId, string BatchCode, decimal QuantityIn, decimal Survivors, decimal LossPieces, decimal LossPercent, bool Estimated);
