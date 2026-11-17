namespace OMTS.Application.Features.Reports.PostSaleIssues;

/// <summary>The groupings of REPORT-03 step 1.</summary>
public enum PostSaleGrouping
{
    Customer,
    Zone,
    Lot,
}

/// <summary>
/// The query of the three REPORT-03 calls (the report, the events of a group, the export). Dates are Vietnam dates: the current month when none is given. <c>GroupBy</c> is customer
/// (default), zone or lot. <c>GroupKey</c> is the <c>key</c> of a group of the report, for the events call only.
/// </summary>
public sealed record PostSaleIssuesQuery(DateOnly? From = null, DateOnly? To = null, string? GroupBy = null, Guid[]? ZoneIds = null, Guid[]? CustomerIds = null, string? GroupKey = null);

/// <summary>The choices that were checked and applied, as the screen and the file show them.</summary>
public sealed record PostSaleIssuesChoices(IReadOnlyList<ReportChoice> Zones, IReadOnlyList<ReportChoice> Customers);

/// <summary>
/// The figures of a group (BR-REPORT-03), each event counted by its own date. By lot, a claim, a price cut and a cancellation apply to a whole invoice that may sell several lots, so their
/// figures and the value of a return are not split between lots: they are null, and the lot has its returns, returned kg and discarded kg only. <see cref="Key"/> is an id or a fixed name
/// (walk-in) for the events call.
/// </summary>
public sealed record PostSaleGroup(
    string Key, string Label, int? ClaimsSent, int? ClaimsAwaiting, int? ClaimsRejected, int? ClaimsAccepted, int ReturnsCount, decimal ReturnedKg, decimal DiscardedKg, decimal? ReturnsValue,
    int? PriceCutsCount, decimal? PriceCutsAmount, int? CancelledCount, decimal? CancelledTotal);

/// <summary>
/// REPORT-03 step 3: worked out when it is shown. <see cref="Totals"/> counts every event of the selection once, whatever the grouping (key "total"; none of its figures is null), so by lot
/// it is not the sum of the rows. <see cref="Message"/> is MSG-GEN-10 when nothing matches (REPORT-03.1); the groups are then empty and the totals 0.
/// </summary>
public sealed record PostSaleIssuesReport(
    DateTimeOffset ComputedAt, DateOnly From, DateOnly To, string GroupBy, PostSaleIssuesChoices Filters, IReadOnlyList<PostSaleGroup> Groups, PostSaleGroup Totals, string? Message,
    string? MessageCode);

public sealed record PostSaleItemCustomer(Guid Id, string Name);

/// <summary>REPORT-03.2: <see cref="Type"/> is claim, return, priceCut or cancellation. <see cref="Status"/> (awaitingResponse, rejected, accepted) is for claims; <see cref="Amount"/> is null for a claim.</summary>
public sealed record PostSaleItemRow(
    string Type, DateOnly Date, Guid InvoiceId, string InvoiceCode, PostSaleItemCustomer? Customer, string? NoteOrReason, decimal? Amount, string? Status);

/// <summary>The events of one group, newest first; <see cref="Message"/> is MSG-GEN-10 when the group has none.</summary>
public sealed record PostSaleItemList(
    DateOnly From, DateOnly To, string GroupBy, string GroupKey, IReadOnlyList<PostSaleItemRow> Items, string? Message, string? MessageCode);
