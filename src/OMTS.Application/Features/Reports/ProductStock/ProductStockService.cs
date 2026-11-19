using OMTS.Application.Common.Interfaces.Persistence;
using OMTS.Application.Common.Messages;
using OMTS.Application.Common.Results;
using OMTS.Application.Features.Authorization;
using OMTS.Application.Features.Authorization.Models;

namespace OMTS.Application.Features.Reports.ProductStock;

/// <summary>
/// The product stock report (KiotViet "Hàng hóa – Xuất nhập tồn"), worked out each time and never stored. The API checks the right (SALES_REPORTS View, or Export for the file: the Cooperative
/// Owner and the Accountant, decision D5 of 19/11/2026). The ledger has no zone, so the figures are cooperative-wide and a user limited to some zones is refused (403 MSG-GEN-08). The period
/// and the products are checked as every sales report does (MSG-GEN-01); no row is MSG-GEN-10.
/// </summary>
public sealed class ProductStockService(ReportFilterResolver filters, IProductStockReportQuery query, TimeProvider timeProvider)
{
    public const string GroupByField = "Grouping";
    public const string CategoryField = "Category";

    public Task<Result<ProductStockReport>> ReportAsync(ProductStockQuery request, CancellationToken cancellationToken) =>
        BuildAsync(request, PermissionAction.View, cancellationToken);

    /// <summary>Shared with the export, which asks for the Export right.</summary>
    internal async Task<Result<ProductStockReport>> BuildAsync(ProductStockQuery request, PermissionAction action, CancellationToken cancellationToken)
    {
        var computedAt = timeProvider.GetUtcNow();
        var grouping = ParseGrouping(request.GroupBy);
        var categoryIds = (request.CategoryIds ?? []).Distinct().ToList();
        var categories = categoryIds.Count > ReportFilterResolver.MaxChoices
            ? new Dictionary<Guid, string>()
            : await query.CategoryNamesAsync(categoryIds, cancellationToken);
        var invalid = new List<string>();
        if (grouping is null)
        {
            invalid.Add(GroupByField);
        }

        if (categoryIds.Count > ReportFilterResolver.MaxChoices || categoryIds.Exists(id => !categories.ContainsKey(id)))
        {
            invalid.Add(CategoryField);
        }

        var filter = await filters.ResolveAsync(
            PermissionFunctions.SalesReports, action, request.From, request.To, null, null, request.ProductIds, invalid, cancellationToken);
        if (!filter.IsSuccess)
        {
            return filter.Error!;
        }

        var f = filter.Value!;
        if (f.ZoneIds is not null)
        {
            return AppError.Forbidden;
        }

        var facts = await query.FactsAsync(f.From, f.To, f.ProductIds, categoryIds, cancellationToken);
        var (groups, totals) = ProductStockCalculator.Build(facts, grouping!.Value);
        var empty = groups.Count == 0 ? UserMessages.NoDataForFilters("period and filters") : null;
        var choices = new ProductStockChoices(f.Products, categoryIds.Select(id => new ReportChoice(id, categories[id])).ToList());
        return new ProductStockReport(computedAt, f.From, f.To, grouping.Value.ToString().ToLowerInvariant(), choices, groups, totals, empty?.Text, empty?.Code);
    }

    /// <summary>By product when none is given; names only, in any letter case.</summary>
    internal static ProductStockGrouping? ParseGrouping(string? value) => value?.Trim().ToLowerInvariant() switch
    {
        null or "" or "product" => ProductStockGrouping.Product,
        "category" => ProductStockGrouping.Category,
        _ => null,
    };
}
