using OMTS.Domain.Enums;

namespace OMTS.Application.Features.Harvest;

/// <summary>The list query of the harvest lots. The period is on the harvest date.</summary>
/// <param name="Weighed">True: weighed lots; false: lots waiting for their weighing.</param>
/// <param name="CloseReason"><c>Unsold</c>, <c>Spoiled</c>, <c>TimeLimitReached</c> or <c>SoldWholeLot</c>.</param>
/// <param name="HasPlan">True: lots recorded from a harvest plan; false: lots outside a plan.</param>
/// <param name="Search">Text in the lot code or the code of the harvest plan.</param>
/// <param name="Sort"><c>code</c> (the default), <c>harvestedAt</c> or <c>remainingKg</c>.</param>
/// <param name="SortDir"><c>asc</c> or <c>desc</c> (the default).</param>
public sealed record HarvestLotListQuery(
    DateOnly? From = null, DateOnly? To = null, Guid? ZoneId = null, Guid? RaftId = null, Guid? ProductId = null, int? StatusId = null, bool? Weighed = null, string? CloseReason = null,
    bool? HasPlan = null, string? Search = null, int Page = 1, int PageSize = 20, string? Sort = null, string? SortDir = null);

/// <summary>A product the lot is split into.</summary>
public sealed record HarvestLotRegisterProduct(Guid ProductId, string Code, string Name);

/// <summary>A lot of the list, with its figures (BR-HARVEST-07).</summary>
public sealed record HarvestLotRegisterItem(
    Guid Id,
    string LotCode,
    DateTimeOffset HarvestedAt,
    DateOnly HarvestDate,
    Guid ZoneId,
    string ZoneCode,
    Guid RaftId,
    string RaftCode,
    Guid CycleId,
    IReadOnlyList<HarvestLotRegisterProduct> Products,
    decimal CageCount,
    decimal EstimatedKg,
    decimal? WeighedKg,
    DateTimeOffset? WeighedAt,
    HarvestLotFigures Figures,
    int StatusId,
    string Status,
    HarvestLotCloseReason? CloseReason,
    DateOnly? CloseDate,
    HarvestLotPlanRef? HarvestPlan,
    string? RecordedByName);

/// <summary>The sums of every lot the filters match, not only the page. <paramref name="WeighedKg"/> counts the weighed lots only.</summary>
public sealed record HarvestLotRegisterTotals(decimal CageCount, decimal EstimatedKg, decimal WeighedKg, decimal SoldKg, decimal RemainingKg, decimal LostKg);

public sealed record HarvestLotRegisterPage(IReadOnlyList<HarvestLotRegisterItem> Items, int Total, int Page, int PageSize, HarvestLotRegisterTotals Totals);

/// <summary>A row of the product ledger on a lot: what the lot gave, sold, wrote off, got back or had taken off at its close. A reversal row undoes the row before it.</summary>
/// <param name="DocumentId">The harvest lot, sales invoice or disposal the row belongs to.</param>
/// <param name="QuantityKg">Signed kg: positive adds to the stock.</param>
/// <param name="PartyName">The customer of a sale or a return, the raft of a harvest, or the reason of a disposal.</param>
/// <param name="SalesOrderCode">The order (DH) the invoice of a Sale row was made from; null for another kind of row and for an invoice without an order.</param>
public sealed record HarvestLotMovementView(
    Guid Id, DateTimeOffset OccurredAt, ProductStockMovementKind Kind, Guid? DocumentId, string DocumentCode, Guid ProductId, string ProductCode, string ProductName, decimal QuantityKg,
    decimal? TransactionPrice, string? PartyName, string? PartyCode, string? SalesOrderCode = null);
