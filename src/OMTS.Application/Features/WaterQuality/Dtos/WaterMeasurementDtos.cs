using OMTS.Application.Common.Messages;

namespace OMTS.Application.Features.WaterQuality.Dtos;

/// <summary>Derived alert state of a measurement (no column): Active = Active and outside, Voided = voided while outside; None otherwise.</summary>
public enum WaterAlertState
{
    None,
    Active,
    Voided,
}

public sealed record AccountRef(Guid Id, string Name);

/// <summary>One row of the WATER-03 history list.</summary>
/// <param name="ThresholdsUsed">The thresholds saved with this measurement (BR-WATER-04).</param>
/// <param name="ValuesOutside">The values outside those thresholds, to mark; also on a voided measurement.</param>
/// <param name="VoidReason">Why it was voided; null when it is Active.</param>
public sealed record WaterMeasurementListItem(
    Guid Id,
    DateTimeOffset MeasuredAt,
    Guid RaftId,
    string RaftCode,
    decimal Ph,
    decimal Salinity,
    decimal Temperature,
    string Status,
    string StatusCode,
    WaterAlertState AlertState,
    string? ViolatedValues,
    AccountRef? MeasuredBy,
    WaterThresholds ThresholdsUsed,
    IReadOnlyList<WaterValueOutside> ValuesOutside,
    string? VoidReason);

/// <summary>A page of the WATER-03 history of a zone and the period it covers.</summary>
/// <param name="Message">MSG-GEN-10 when no measurement matches (WATER-03.4).</param>
/// <param name="MessageCode">The code of <paramref name="Message"/>.</param>
public sealed record WaterHistoryPage(
    IReadOnlyList<WaterMeasurementListItem> Items, int Page, int PageSize, int Total, DateOnly From, DateOnly To, string? Message, string? MessageCode);

/// <summary>One zone on the WATER-03 overview.</summary>
/// <param name="LatestMeasuredAt">The measurement time of its latest active measurement; null when it has none.</param>
/// <param name="HasWaterAlert">BR-WATER-04: the zone is In Use and that measurement is outside its own thresholds.</param>
/// <param name="ValuesOutside">Those values, with their allowed range; empty without a water alert.</param>
public sealed record WaterZoneOverviewItem(
    Guid ZoneId,
    string ZoneCode,
    string ZoneName,
    string ZoneStatus,
    string ZoneStatusCode,
    DateTimeOffset? LatestMeasuredAt,
    bool HasWaterAlert,
    IReadOnlyList<WaterValueOutside> ValuesOutside);

/// <summary>WATER-03.2 detail: all fields, a short-lived photo URL and the thresholds of the measurement.</summary>
/// <param name="AlertedAt">When the last water alert of this measurement was sent; null when none is standing or none was sent.</param>
public sealed record WaterMeasurementDetail(
    Guid Id,
    Guid ZoneId,
    string ZoneCode,
    string ZoneName,
    Guid RaftId,
    string RaftCode,
    DateTimeOffset MeasuredAt,
    decimal Ph,
    decimal Salinity,
    decimal Temperature,
    string Status,
    string StatusCode,
    string PhotoUrl,
    WaterThresholds ThresholdsUsed,
    WaterAlertState AlertState,
    bool ThresholdExceeded,
    string? ViolatedValues,
    DateTimeOffset? AlertedAt,
    AccountRef? MeasuredBy,
    string? VoidReason);

/// <summary>Result of WATER-01: <see cref="Replayed"/> when the client operation was already saved (answered 200, not 201).</summary>
public sealed record RecordedWaterMeasurement(WaterMeasurementDetail Measurement, bool Replayed);
