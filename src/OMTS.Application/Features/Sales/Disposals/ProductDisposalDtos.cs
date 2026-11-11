namespace OMTS.Application.Features.Sales.Disposals;

/// <summary>A line as entered: the product, the harvest lot of the disposal's zone it is taken from, and the kg.</summary>
public sealed record ProductDisposalLineInput(Guid? ProductId, Guid? HarvestLotId, decimal? Kg, string? Note = null);

/// <summary>
/// Saves a disposal as a Draft or, with <see cref="Complete"/>, completes it in the same call; a Completed disposal stays editable (<see cref="RowVersion"/> required) and its kg are written again.
/// A disposal belongs to one zone (required); every line names a harvest lot of that zone and a product the lot was split into. To complete it needs a reason and at least one line.
/// <see cref="Code"/>: 1–30 ASCII letters, digits, "-", "_" or "."; unique regardless of case; empty = the next XHS code, or the stored one on an update.
/// <see cref="DisposedAt"/> is when the oysters left, never later than now; empty = now, or the stored time on an update.
/// </summary>
public sealed record SaveProductDisposalRequest(
    string? Code,
    DateTimeOffset? DisposedAt,
    Guid? ZoneId,
    string? Reason,
    string? Notes,
    IReadOnlyList<ProductDisposalLineInput>? Lines,
    bool Complete = false,
    string? RowVersion = null);

public sealed record CompleteProductDisposalRequest(string? RowVersion = null);

public sealed record CancelProductDisposalRequest(string? Reason, string? RowVersion = null);

public sealed record ProductDisposalLineView(
    Guid Id,
    int LineNumber,
    Guid ProductId,
    string ProductCode,
    string ProductName,
    Guid HarvestLotId,
    string LotCode,
    decimal Kg,
    string? Note);

public sealed record ProductDisposalView(
    Guid Id,
    string Code,
    int StatusId,
    string Status,
    DateTimeOffset DisposedAt,
    Guid ZoneId,
    string ZoneCode,
    string? Reason,
    string? Notes,
    IReadOnlyList<ProductDisposalLineView> Lines,
    decimal TotalKg,
    DateTimeOffset CreatedAt,
    string? CreatedBy,
    DateTimeOffset? CompletedAt,
    DateTimeOffset? CancelledAt,
    string? CancelReason,
    string RowVersion);

/// <summary>A row of the disposal list: the disposal without its lines.</summary>
public sealed record ProductDisposalListItem(
    Guid Id,
    string Code,
    int StatusId,
    string Status,
    DateTimeOffset DisposedAt,
    Guid ZoneId,
    string ZoneCode,
    string? Reason,
    string? Notes,
    decimal TotalKg,
    DateTimeOffset CreatedAt,
    string? CreatedBy,
    string RowVersion);

public enum ProductDisposalStatusFilter
{
    All,
    Draft,
    Completed,
    Cancelled,
}

/// <summary>The disposal list and export query. The period is on the business date of <c>DisposedAt</c>. A Zone Manager sees only the disposals of its zones.</summary>
/// <param name="Search">Text in the code, the notes or the reason.</param>
/// <param name="Status">All (the default), Draft, Completed or Cancelled.</param>
/// <param name="ProductId">Disposals that have a line of the product.</param>
/// <param name="CreatedById">The account that recorded the disposal.</param>
/// <param name="SortBy">code (the default, newest first), disposedAt, totalKg or status.</param>
/// <param name="SortDir">asc or desc; the default is desc.</param>
public sealed record ProductDisposalListQuery(
    string? Search = null,
    ProductDisposalStatusFilter Status = ProductDisposalStatusFilter.All,
    DateOnly? From = null,
    DateOnly? To = null,
    Guid? ZoneId = null,
    Guid? ProductId = null,
    Guid? CreatedById = null,
    string? SortBy = null,
    string? SortDir = null,
    int Page = 1,
    int PageSize = 20);

/// <param name="TotalKg">Σ the kg of the Completed disposals that match, not only the page.</param>
public sealed record ProductDisposalListSummary(decimal TotalKg);

public sealed record ProductDisposalPage(IReadOnlyList<ProductDisposalListItem> Items, int Page, int PageSize, int Total, ProductDisposalListSummary Summary);

public sealed record ProductDisposalNextCode(string Code);
