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

/// <summary>Someone with the edit form open (NFR-13); the record is never locked.</summary>
public sealed record RecordEditorView(Guid AccountId, string Name, DateTimeOffset StartedAt);

/// <summary>WATER-03.2 detail: all fields, a short-lived photo URL, the thresholds of the measurement and the current editors.</summary>
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
    string? VoidReason,
    IReadOnlyList<RecordEditorView> CurrentEditors,
    UserMessage? EditingNotice);

/// <summary>Result of WATER-01: <see cref="Replayed"/> when the client operation was already saved (answered 200, not 201).</summary>
public sealed record RecordedWaterMeasurement(WaterMeasurementDetail Measurement, bool Replayed);

/// <summary>Other people editing the record, with MSG58 for the first of them.</summary>
public sealed record EditingStatus(IReadOnlyList<RecordEditorView> OtherEditors, UserMessage? Notice);
