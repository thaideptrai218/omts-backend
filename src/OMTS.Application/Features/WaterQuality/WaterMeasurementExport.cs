using OMTS.Application.Common;
using OMTS.Application.Common.Interfaces.Reports;
using OMTS.Application.Features.WaterQuality.Dtos;

namespace OMTS.Application.Features.WaterQuality;

/// <summary>WATER-03 export content: a "Measurements" sheet (the history rows) and a "Monthly summary" of the active values.</summary>
public static class WaterMeasurementExport
{
    public const string MeasurementsSheet = "Measurements";
    public const string MonthlySummarySheet = "Monthly summary";

    public static readonly IReadOnlyList<string> MeasurementHeaders =
    [
        "Measured at", "Raft", "pH", "Salinity (‰)", "Temperature (°C)", "Status", "Alert", "Violated values", "Measured by",
    ];

    public static readonly IReadOnlyList<string> SummaryHeaders =
    [
        "Month", "pH average", "pH minimum", "pH maximum", "pH count",
        "Salinity average", "Salinity minimum", "Salinity maximum", "Salinity count", "No data",
    ];

    /// <param name="rows">History rows, oldest first.</param>
    /// <param name="months">Monthly chart periods of the active values.</param>
    public static IReadOnlyList<SpreadsheetSheet> Sheets(IReadOnlyList<WaterMeasurementListItem> rows, IReadOnlyList<WaterChartPeriod> months) =>
    [
        new SpreadsheetSheet(MeasurementsSheet, MeasurementHeaders, rows.Select(MeasurementRow).ToList()),
        new SpreadsheetSheet(MonthlySummarySheet, SummaryHeaders, months.Select(SummaryRow).ToList()),
    ];

    /// <summary>water-{zoneCode}-{from}-{to}.xlsx with the zone code reduced to file-name-safe characters.</summary>
    public static string FileName(string zoneCode, WaterPeriod period)
    {
        var code = new string(zoneCode.Select(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_' ? c : '_').ToArray());
        return $"water-{code}-{period.From:yyyy-MM-dd}-{period.To:yyyy-MM-dd}.xlsx";
    }

    private static IReadOnlyList<object?> MeasurementRow(WaterMeasurementListItem m) =>
    [
        m.MeasuredAt.ToOffset(BusinessClock.UtcOffset).DateTime, m.RaftCode, m.Ph, m.Salinity, m.Temperature, m.Status,
        m.AlertState == WaterAlertState.None ? null : m.AlertState.ToString(), m.ViolatedValues, m.MeasuredBy?.Name,
    ];

    private static IReadOnlyList<object?> SummaryRow(WaterChartPeriod p) =>
    [
        p.Period, p.Ph.Average, p.Ph.Minimum, p.Ph.Maximum, p.Ph.Count,
        p.Salinity.Average, p.Salinity.Minimum, p.Salinity.Maximum, p.Salinity.Count, p.Missing,
    ];
}
