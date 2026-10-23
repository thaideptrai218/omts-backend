using Microsoft.Extensions.Logging;
using OMTS.Application.Common;
using OMTS.Application.Common.Interfaces.Persistence;
using OMTS.Application.Common.Interfaces.Reports;
using OMTS.Application.Common.Messages;
using OMTS.Application.Common.Results;
using OMTS.Application.Features.AccessManagement.Dtos;
using OMTS.Application.Features.Authorization;
using OMTS.Application.Features.Authorization.Models;
using OMTS.Application.Features.WaterQuality.Dtos;
using OMTS.Domain.Entities.Farming;
using S = OMTS.Domain.Constants.StatusIds.WaterMeasurement;

namespace OMTS.Application.Features.WaterQuality;

/// <summary>Filters of the ENV-03 history list and export: local dates (default last 3 months) and statuses (default Confirmed).</summary>
public sealed record WaterHistoryQuery(string? From, string? To, IReadOnlyList<string>? Status, int Page = 1, int PageSize = 20);

public sealed record ExportFile(string FileName, string ContentType, byte[] Content);

/// <summary>
/// ENV-03 – water history, detail, chart and Excel export of a zone. Charts and exports use only Confirmed
/// values: Drafts, corrected versions and voided records never count.
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
    public const int MaxChartYears = 10;
    public const string StatusField = "Status";
    public const string GroupByField = "Group by";

    // Exports never contain Drafts or voided records, whatever the status filter asks.
    private static readonly int[] ExportableStatuses = [S.Confirmed, S.Superseded];

    public async Task<Result<Paged<WaterMeasurementListItem>>> SearchAsync(Guid zoneId, WaterHistoryQuery query, CancellationToken cancellationToken)
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
        return new Paged<WaterMeasurementListItem>(await views.ListItemsAsync(items, cancellationToken), page, pageSize, total);
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

    /// <summary>Default range: the last 12 months (whole months when grouped by month); by day at most 366 days.</summary>
    public async Task<Result<WaterChart>> ChartAsync(Guid zoneId, string? from, string? to, string? groupBy, CancellationToken cancellationToken)
    {
        var zone = await measurements.GetZoneAsync(zoneId, track: false, cancellationToken);
        if (zone is null)
        {
            return AppError.NotFound;
        }

        WaterChartGrouping grouping;
        if (string.IsNullOrWhiteSpace(groupBy))
        {
            grouping = WaterChartGrouping.Month;
        }
        else if (!Enum.TryParse(groupBy.Trim(), ignoreCase: true, out grouping) || !Enum.IsDefined(grouping))
        {
            return AppError.Invalid(GroupByField, "Use month or day.");
        }

        var period = WaterPeriod.Parse(from, to, timeProvider.Today(), end => grouping == WaterChartGrouping.Month
            ? new DateOnly(end.Year, end.Month, 1).AddMonths(-11)
            : end.AddMonths(-12).AddDays(1));
        if (!period.IsSuccess)
        {
            return period.Error!;
        }

        if (grouping == WaterChartGrouping.Day && period.Value!.Days > WaterChartBuilder.MaxDays)
        {
            return AppError.Invalid(GroupByField, $"Grouping by day covers at most {WaterChartBuilder.MaxDays} days.");
        }

        if (grouping == WaterChartGrouping.Month && period.Value!.From.AddYears(MaxChartYears) < period.Value.To)
        {
            return AppError.Invalid(GroupByField, $"Grouping by month covers at most {MaxChartYears} years.");
        }

        var samples = await ConfirmedSamplesAsync(zoneId, period.Value!, cancellationToken);
        return new WaterChart(
            zoneId, period.Value!.From, period.Value.To, grouping, WaterThresholds.Of(zone),
            WaterChartBuilder.Build(samples, period.Value, grouping));
    }

    /// <summary>The history rows (Confirmed, and Corrected when asked) and the monthly summary as .xlsx.</summary>
    public async Task<Result<ExportFile>> ExportAsync(Guid zoneId, WaterHistoryQuery query, CancellationToken cancellationToken)
    {
        var filter = await FilterAsync(zoneId, query, cancellationToken);
        if (!filter.IsSuccess)
        {
            return filter.Error!;
        }

        var (zone, period, statusIds) = filter.Value!;
        var exported = statusIds.Intersect(ExportableStatuses).ToList();
        var rows = exported.Count == 0
            ? []
            : await measurements.ListAsync(zoneId, period.FromInstant, period.ToExclusive, exported, cancellationToken);
        var months = WaterChartBuilder.Build(await ConfirmedSamplesAsync(zoneId, period, cancellationToken), period, WaterChartGrouping.Month);
        var sheets = WaterMeasurementExport.Sheets(await views.ListItemsAsync(rows, cancellationToken), months);

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
        var zone = await measurements.GetZoneAsync(zoneId, track: false, cancellationToken);
        if (zone is null)
        {
            return AppError.NotFound;
        }

        var period = WaterPeriod.Parse(query.From, query.To, timeProvider.Today(), end => end.AddMonths(-3));
        if (!period.IsSuccess)
        {
            return period.Error!;
        }

        var statusIds = WaterMeasurementStatuses.ParseFilter(query.Status);
        if (statusIds is null)
        {
            return AppError.Invalid(StatusField, "Use Draft, Confirmed, Corrected, Voided or All.");
        }

        return (zone, period.Value!, statusIds);
    }

    private async Task<IReadOnlyList<WaterSample>> ConfirmedSamplesAsync(Guid zoneId, WaterPeriod period, CancellationToken cancellationToken) =>
        (await measurements.ListAsync(zoneId, period.FromInstant, period.ToExclusive, [S.Confirmed], cancellationToken))
            .Where(m => m.Ph is not null && m.Salinity is not null)
            .Select(m => new WaterSample(m.MeasuredAt, m.Ph!.Value, m.Salinity!.Value))
            .ToList();
}
