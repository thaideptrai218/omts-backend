using System.Globalization;
using OMTS.Application.Common.Interfaces.Persistence;
using OMTS.Application.Common.Messages;
using OMTS.Application.Common.Results;
using OMTS.Application.Features.Authorization;
using OMTS.Application.Features.Authorization.Models;

namespace OMTS.Application.Features.Reports.PriceTrends;

/// <summary>
/// REPORT-02 – the unit prices of each product by day, week or month, with the sold kg, the average price weighted by kg and the lowest and highest price, worked out each time they are shown
/// (BR-REPORT-02). The API checks the role (SALES_REPORTS View, or Export for the file); this checks the zones of the user's pairs (BR-FARM-08), the period and the choices (MSG-GEN-01), and
/// finds no line (MSG-GEN-10, REPORT-02.1). Read only.
/// </summary>
public sealed class PriceTrendsService(ReportFilterResolver filters, IPriceTrendsQuery query, TimeProvider timeProvider)
{
    public const string UnitField = "Time unit";
    public const string PeriodKeyField = "Period key";

    public Task<Result<PriceTrendsReport>> ReportAsync(PriceTrendsQuery request, CancellationToken cancellationToken) =>
        BuildAsync(request, PermissionAction.View, cancellationToken);

    /// <summary>REPORT-02.2: the invoice lines counted in one period of one product, with the same selection; MSG-GEN-10 when there are none.</summary>
    public async Task<Result<PriceTrendLineList>> LinesAsync(PriceTrendsQuery request, CancellationToken cancellationToken)
    {
        var unit = ParseUnit(request.Unit);
        var invalid = new List<string>();
        if (unit is null)
        {
            invalid.Add(UnitField);
        }

        if (request.ProductId is null)
        {
            invalid.Add(ReportFilterResolver.ProductField);
        }

        var periodKey = request.PeriodKey?.Trim();
        if (!DateOnly.TryParseExact(periodKey, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var periodDate))
        {
            invalid.Add(PeriodKeyField);
        }

        // The product of the lines is also one of the choices, so an unknown id is MSG-GEN-01 with the others.
        var productIds = (request.ProductIds ?? Array.Empty<Guid>()).ToList();
        if (request.ProductId is { } chosenProduct)
        {
            productIds.Add(chosenProduct);
        }

        var filter = await ResolveAsync(request, productIds, PermissionAction.View, invalid, cancellationToken);
        if (!filter.IsSuccess)
        {
            return filter.Error!;
        }

        // The unit, the product and the key are known to be valid here: each was an invalid field above.
        var (f, productId, periodUnit) = (filter.Value!, request.ProductId!.Value, unit!.Value);
        var lines = PriceTrendsCalculator.LinesOf(await query.LinesAsync(f, cancellationToken), periodUnit, productId, periodDate);
        var empty = lines.Count == 0 ? UserMessages.NoDataForFilters("product and period") : null;
        return new PriceTrendLineList(f.From, f.To, Name(periodUnit), productId, periodKey!, lines, empty?.Text, empty?.Code);
    }

    /// <summary>Shared with the export (<see cref="PriceTrendsExportService"/>), which asks for the Export right.</summary>
    internal async Task<Result<PriceTrendsReport>> BuildAsync(PriceTrendsQuery request, PermissionAction action, CancellationToken cancellationToken)
    {
        // The clock is read before any figure, so computedAt is not later than the figures it names.
        var computedAt = timeProvider.GetUtcNow();
        var unit = ParseUnit(request.Unit);
        var filter = await ResolveAsync(request, request.ProductIds ?? Array.Empty<Guid>(), action, unit is null ? new[] { UnitField } : Array.Empty<string>(), cancellationToken);
        if (!filter.IsSuccess)
        {
            return filter.Error!;
        }

        var (f, periodUnit) = (filter.Value!, unit!.Value);
        var series = PriceTrendsCalculator.Build(await query.LinesAsync(f, cancellationToken), periodUnit);
        var empty = series.Count == 0 ? UserMessages.NoDataForFilters("period and filters") : null;
        return new PriceTrendsReport(computedAt, f.From, f.To, Name(periodUnit), new PriceTrendsChoices(f.Zones, f.Products), series, empty?.Text, empty?.Code);
    }

    private Task<Result<ReportFilter>> ResolveAsync(
        PriceTrendsQuery request, IReadOnlyCollection<Guid> productIds, PermissionAction action, IReadOnlyList<string> invalid, CancellationToken cancellationToken) =>
        filters.ResolveAsync(PermissionFunctions.SalesReports, action, request.From, request.To, request.ZoneIds, null, productIds, invalid, cancellationToken);

    /// <summary>By day when none is given; names only, in any letter case (never a number).</summary>
    internal static ReportPeriodUnit? ParseUnit(string? value) => string.IsNullOrWhiteSpace(value) ? ReportPeriodUnit.Day : ReportPeriodBuckets.Parse(value);

    internal static string Name(ReportPeriodUnit unit) => unit.ToString().ToLowerInvariant();
}
