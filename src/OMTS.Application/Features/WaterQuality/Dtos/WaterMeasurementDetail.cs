using OMTS.Application.Common.Messages;
using OMTS.Domain.Enums;

namespace OMTS.Application.Features.WaterQuality.Dtos;

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
