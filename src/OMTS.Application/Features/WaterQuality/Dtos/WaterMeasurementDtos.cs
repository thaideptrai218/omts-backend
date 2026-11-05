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
    AccountRef? MeasuredBy);

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
