using Microsoft.Extensions.Logging;
using OMTS.Application.Common;
using OMTS.Application.Common.Interfaces.Persistence;
using OMTS.Application.Common.Interfaces.Reports;
using OMTS.Application.Common.Messages;
using OMTS.Application.Common.Results;
using OMTS.Application.Features.Authorization;
using OMTS.Application.Features.Authorization.Models;
using OMTS.Application.Features.WaterQuality.Dtos;
using OMTS.Domain.Entities.Farming;
using S = OMTS.Domain.Constants.StatusIds.WaterMeasurement;

namespace OMTS.Application.Features.WaterQuality;

/// <summary>Filters of the WATER-03 history list and export: local dates (default last 3 months) and statuses (default Active and Voided).</summary>
public sealed record WaterHistoryQuery(string? From, string? To, IReadOnlyList<string>? Status, int Page = 1, int PageSize = 20);

public sealed record ExportFile(string FileName, string ContentType, byte[] Content);

/// <summary>
/// WATER-03 – water history, detail, chart and Excel export of a zone. The list and the export hold the voided
/// measurements with their reason; the chart uses only the active ones (BR-WATER-06).
/// </summary>
public sealed class WaterMeasurementQueryService(
    IWaterMeasurementRepository measurements,
    ZoneAccessGuard zoneAccess,
    WaterMeasurementViewBuilder views,
    ISpreadsheetWriter spreadsheets,
    TimeProvider timeProvider,
    ILogger<WaterMeasurementQueryService> logger)
{
    public const int MaxPageSize = 100;
    public const string StatusField = "Status";

    public async Task<Result<WaterHistoryPage>> SearchAsync(Guid zoneId, WaterHistoryQuery query, CancellationToken cancellationToken)
    {
        var filter = await FilterAsync(zoneId, query, cancellationToken);
        if (!filter.IsSuccess)
        {
            return filter.Error!;
        }

        var (_, period, statusIds) = filter.Value!;
        // The page is capped so (page - 1) * pageSize cannot overflow into a negative OFFSET.
        var (page, pageSize) = (Math.Clamp(query.Page, 1, int.MaxValue / MaxPageSize), Math.Clamp(query.PageSize, 1, MaxPageSize));
        var (items, total) = await measurements.SearchAsync(zoneId, period.FromInstant, period.ToExclusive, statusIds, page, pageSize, cancellationToken);
        var empty = total == 0 ? UserMessages.NoDataForFilters(query.Status is { Count: > 0 } ? "zone, period and status" : "zone and period") : null;
        return new WaterHistoryPage(await views.ListItemsAsync(items, cancellationToken), page, pageSize, total, period.From, period.To, empty?.Text, empty?.Code);
    }

    public async Task<Result<WaterMeasurementDetail>> GetAsync(Guid id, CancellationToken cancellationToken)
    {
        var measurement = await measurements.GetAsync(id, cancellationToken);
        if (measurement is null)
        {
            return AppError.NotFound;
        }

        return await zoneAccess.AllowsAsync(PermissionFunctions.WaterMeasurements, PermissionAction.View, measurement.ZoneId, cancellationToken)
            ? await views.DetailAsync(measurement, measurement.Zone, cancellationToken)
            : AppError.Forbidden;
    }

    /// <summary>The active measurements of the period as raw points, for pH, salinity and temperature; the period defaults as in the list.</summary>
    public async Task<Result<WaterChart>> ChartAsync(Guid zoneId, string? from, string? to, CancellationToken cancellationToken)
    {
        var scope = await ZoneAndPeriodAsync(zoneId, from, to, cancellationToken);
        if (!scope.IsSuccess)
        {
            return scope.Error!;
        }

        var (zone, period) = scope.Value;
        var active = await measurements.ListAsync(zoneId, period.FromInstant, period.ToExclusive, [S.Active], cancellationToken);
        return new WaterChart(
            zoneId, period.From, period.To, WaterThresholds.Of(zone),
            active.Select(m => new WaterChartPoint(m.Id, m.MeasuredAt, m.Ph, m.Salinity, m.Temperature)).ToList());
    }

    /// <summary>The measurements of the list, in its order, as .xlsx (WATER-03.3).</summary>
    public async Task<Result<ExportFile>> ExportAsync(Guid zoneId, WaterHistoryQuery query, CancellationToken cancellationToken)
    {
        var filter = await FilterAsync(zoneId, query, cancellationToken);
        if (!filter.IsSuccess)
        {
            return filter.Error!;
        }

        var (zone, period, statusIds) = filter.Value!;
        var oldestFirst = await measurements.ListAsync(zoneId, period.FromInstant, period.ToExclusive, statusIds, cancellationToken);
        var sheets = WaterMeasurementExport.Sheets(await views.ListItemsAsync(oldestFirst.Reverse().ToList(), cancellationToken));

        try
        {
            return new ExportFile(WaterMeasurementExport.FileName(zone.Code, period), ISpreadsheetWriter.ContentType, spreadsheets.Write(sheets));
        }
        catch (Exception exception)
        {
            logger.LogError(exception, "Water measurement export of zone {ZoneId} failed", zoneId);
            return new AppError(ErrorKind.Internal, UserMessages.FileOperationFailed);
        }
    }

    private async Task<Result<(FarmingZone Zone, WaterPeriod Period, IReadOnlyList<int> StatusIds)>> FilterAsync(
        Guid zoneId, WaterHistoryQuery query, CancellationToken cancellationToken)
    {
        var scope = await ZoneAndPeriodAsync(zoneId, query.From, query.To, cancellationToken);
        if (!scope.IsSuccess)
        {
            return scope.Error!;
        }

        var statusIds = WaterMeasurementStatuses.ParseFilter(query.Status);
        if (statusIds is null)
        {
            return AppError.Invalid(StatusField, "Use Active, Voided or All.");
        }

        return (scope.Value.Zone, scope.Value.Period, statusIds);
    }

    private async Task<Result<(FarmingZone Zone, WaterPeriod Period)>> ZoneAndPeriodAsync(
        Guid zoneId, string? from, string? to, CancellationToken cancellationToken)
    {
        var zone = await measurements.GetZoneAsync(zoneId, track: false, cancellationToken);
        if (zone is null)
        {
            return AppError.NotFound;
        }

        var period = WaterPeriod.Parse(from, to, timeProvider.Today(), end => end.AddMonths(-3));
        return period.IsSuccess ? (zone, period.Value!) : period.Error!;
    }
}
