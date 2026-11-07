using OMTS.Application.Common;
using OMTS.Application.Common.Interfaces.Reports;
using OMTS.Application.Features.WaterQuality.Dtos;

namespace OMTS.Application.Features.WaterQuality;

/// <summary>WATER-03.3 export content: the measurements shown, each with its status and the thresholds saved with it.</summary>
public static class WaterMeasurementExport
{
    public const string MeasurementsSheet = "Measurements";

    public static readonly IReadOnlyList<string> MeasurementHeaders =
    [
        "Measured at", "Raft", "pH", "Salinity (‰)", "Temperature (°C)", "Status", "Void reason", "Alert", "Violated values", "Measured by",
        "pH minimum", "pH maximum", "Salinity minimum (‰)", "Salinity maximum (‰)", "Temperature minimum (°C)", "Temperature maximum (°C)",
    ];

    /// <param name="rows">History rows, in the order of the list.</param>
    public static IReadOnlyList<SpreadsheetSheet> Sheets(IReadOnlyList<WaterMeasurementListItem> rows) =>
    [
        new SpreadsheetSheet(MeasurementsSheet, MeasurementHeaders, rows.Select(MeasurementRow).ToList()),
    ];

    /// <summary>water-{zoneCode}-{from}-{to}.xlsx with the zone code reduced to file-name-safe characters.</summary>
    public static string FileName(string zoneCode, WaterPeriod period)
    {
        var code = new string(zoneCode.Select(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_' ? c : '_').ToArray());
        return $"water-{code}-{period.From:yyyy-MM-dd}-{period.To:yyyy-MM-dd}.xlsx";
    }

    private static IReadOnlyList<object?> MeasurementRow(WaterMeasurementListItem m) =>
    [
        m.MeasuredAt.ToOffset(BusinessClock.UtcOffset).DateTime, m.RaftCode, m.Ph, m.Salinity, m.Temperature, m.Status, m.VoidReason,
        m.AlertState == WaterAlertState.None ? null : m.AlertState.ToString(), m.ViolatedValues, m.MeasuredBy?.Name,
        m.ThresholdsUsed.PhMin, m.ThresholdsUsed.PhMax, m.ThresholdsUsed.SalinityMin, m.ThresholdsUsed.SalinityMax,
        m.ThresholdsUsed.TemperatureMin, m.ThresholdsUsed.TemperatureMax,
    ];
}
