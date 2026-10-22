using OMTS.Application.Common.Messages;
using OMTS.Domain.Enums;

namespace OMTS.Application.Features.WaterQuality.Dtos;

/// <summary>
/// Derived alert state (no column): Active = Confirmed and exceeded, Corrected = replaced by a correction
/// version, Voided = voided; None when the values were within the thresholds (or not confirmed yet).
/// </summary>
public enum WaterAlertState
{
    None,
    Active,
    Corrected,
    Voided,
}

public sealed record AccountRef(Guid Id, string Name);

/// <summary>One row of the ENV-03 history list.</summary>
public sealed record WaterMeasurementListItem(
    Guid Id,
    DateTimeOffset MeasuredAt,
    decimal? Ph,
    decimal? Salinity,
    string? MeasurementPoint,
    string Status,
    string StatusCode,
    MeasurementInputSource InputSource,
    WaterAlertState AlertState,
    string? ViolatedValues,
    AccountRef? MeasuredBy,
    AccountRef? ConfirmedBy,
    int RecordVersion,
    bool HasPhoto);

/// <summary>A version of the correction chain (oldest first in <see cref="WaterMeasurementDetail.Versions"/>).</summary>
public sealed record WaterMeasurementVersion(
    Guid Id,
    int RecordVersion,
    string Status,
    string StatusCode,
    DateTimeOffset MeasuredAt,
    decimal? Ph,
    decimal? Salinity,
    WaterAlertState AlertState,
    string? ViolatedValues,
    string? CorrectionReason,
    string? VoidReason,
    AccountRef? ConfirmedBy,
    DateTimeOffset? ConfirmedAt,
    DateTimeOffset CreatedAt);

/// <summary>The first OCR draft, kept unchanged for comparison with the confirmed values.</summary>
public sealed record OcrDraftValues(decimal? Ph, decimal? Salinity, DateTimeOffset? MeasuredAt);

/// <summary>Someone with the edit form open (NFR-13); the record is never locked.</summary>
public sealed record RecordEditorView(Guid AccountId, string Name, DateTimeOffset StartedAt);

/// <summary>ENV-03 detail: all fields, a short-lived photo URL, the thresholds used, the version chain and the current editors.</summary>
public sealed record WaterMeasurementDetail(
    Guid Id,
    Guid ZoneId,
    string ZoneCode,
    string ZoneName,
    string? MeasurementPoint,
    DateTimeOffset MeasuredAt,
    decimal? Ph,
    decimal? Salinity,
    string Status,
    string StatusCode,
    MeasurementInputSource InputSource,
    string? PhotoUrl,
    OcrDraftValues OcrDraft,
    WaterThresholds? ThresholdsUsed,
    WaterAlertState AlertState,
    bool ThresholdExceeded,
    string? ViolatedValues,
    DateTimeOffset? AlertedAt,
    AccountRef? MeasuredBy,
    AccountRef? ConfirmedBy,
    DateTimeOffset? ConfirmedAt,
    int RecordVersion,
    Guid? CorrectedFromMeasurementId,
    string? CorrectionReason,
    string? VoidReason,
    bool IsLatestVersion,
    IReadOnlyList<WaterMeasurementVersion> Versions,
    IReadOnlyList<RecordEditorView> CurrentEditors,
    UserMessage? EditingNotice);

/// <summary>Result of ENV-01 create: <see cref="Replayed"/> when the client operation was already saved (answered 200, not 201).</summary>
public sealed record RecordedWaterMeasurement(WaterMeasurementDetail Measurement, bool Replayed);

/// <summary>Other people editing the record, with MSG58 for the first of them.</summary>
public sealed record EditingStatus(IReadOnlyList<RecordEditorView> OtherEditors, UserMessage? Notice);
