using OMTS.Application.Common.Interfaces.Persistence;
using OMTS.Application.Common.Messages;
using OMTS.Application.Common.Results;
using OMTS.Application.Features.Authorization;
using OMTS.Application.Features.Authorization.Models;

namespace OMTS.Application.Features.Reports.PostSaleIssues;

/// <summary>
/// REPORT-03 – the quality claims, sales returns, price cuts after sale and cancelled invoices of a period by customer, zone or harvest lot, each counted by the date of its own event and worked
/// out each time it is shown (BR-REPORT-03; the sales report REPORT-01 counts them in the period of their invoice). The API checks the role (SALES_REPORTS View, or Export for the file); this
/// checks the zones of the user's pairs (BR-FARM-08), the period and the choices (MSG-GEN-01), and finds no event (MSG-GEN-10, REPORT-03.1). Read only.
/// </summary>
public sealed class PostSaleIssuesService(ReportFilterResolver filters, IPostSaleIssuesQuery query, TimeProvider timeProvider)
{
    public const string GroupByField = "Grouping";
    public const string GroupKeyField = "Group key";

    public Task<Result<PostSaleIssuesReport>> ReportAsync(PostSaleIssuesQuery request, CancellationToken cancellationToken) =>
        BuildAsync(request, PermissionAction.View, cancellationToken);

    /// <summary>REPORT-03.2: the events of one group of the same selection, newest first; MSG-GEN-10 when the group has none.</summary>
    public async Task<Result<PostSaleItemList>> ItemsAsync(PostSaleIssuesQuery request, CancellationToken cancellationToken)
    {
        var grouping = ParseGrouping(request.GroupBy);
        var invalid = new List<string>();
        if (grouping is null)
        {
            invalid.Add(GroupByField);
        }

        if (string.IsNullOrWhiteSpace(request.GroupKey))
        {
            invalid.Add(GroupKeyField);
        }

        var filter = await ResolveAsync(request, PermissionAction.View, invalid, cancellationToken);
        if (!filter.IsSuccess)
        {
            return filter.Error!;
        }

        // Both are known to be given: a missing grouping or key is an invalid field above.
        var (f, groupBy, key) = (filter.Value!, grouping!.Value, request.GroupKey!.Trim());
        var items = PostSaleIssuesCalculator.ItemsOf(await query.FactsAsync(f, cancellationToken), groupBy, key);
        var empty = items.Count == 0 ? UserMessages.NoDataForFilters("group") : null;
        return new PostSaleItemList(f.From, f.To, Name(groupBy), key, items, empty?.Text, empty?.Code);
    }

    /// <summary>Shared with the export (<see cref="PostSaleIssuesExportService"/>), which asks for the Export right.</summary>
    internal async Task<Result<PostSaleIssuesReport>> BuildAsync(PostSaleIssuesQuery request, PermissionAction action, CancellationToken cancellationToken)
    {
        // The clock is read before any figure, so computedAt is not later than the figures it names.
        var computedAt = timeProvider.GetUtcNow();
        var grouping = ParseGrouping(request.GroupBy);
        var filter = await ResolveAsync(request, action, grouping is null ? new[] { GroupByField } : Array.Empty<string>(), cancellationToken);
        if (!filter.IsSuccess)
        {
            return filter.Error!;
        }

        var (f, groupBy) = (filter.Value!, grouping!.Value);
        var facts = await query.FactsAsync(f, cancellationToken);
        var calculation = PostSaleIssuesCalculator.Build(facts, groupBy);
        // No row to show is no data (REPORT-03.1), whatever the grouping.
        var empty = calculation.Groups.Count == 0 ? UserMessages.NoDataForFilters("period and filters") : null;
        return new PostSaleIssuesReport(
            computedAt, f.From, f.To, Name(groupBy), new PostSaleIssuesChoices(f.Zones, f.Customers), calculation.Groups, calculation.Totals, empty?.Text, empty?.Code);
    }

    private Task<Result<ReportFilter>> ResolveAsync(
        PostSaleIssuesQuery request, PermissionAction action, IReadOnlyList<string> invalid, CancellationToken cancellationToken) =>
        filters.ResolveAsync(PermissionFunctions.SalesReports, action, request.From, request.To, request.ZoneIds, request.CustomerIds, null, invalid, cancellationToken);

    /// <summary>By customer when none is given; names only, in any letter case (never a number).</summary>
    internal static PostSaleGrouping? ParseGrouping(string? value) => value?.Trim().ToLowerInvariant() switch
    {
        null or "" or "customer" => PostSaleGrouping.Customer,
        "zone" => PostSaleGrouping.Zone,
        "lot" => PostSaleGrouping.Lot,
        _ => null,
    };

    internal static string Name(PostSaleGrouping grouping) => grouping.ToString().ToLowerInvariant();
}
