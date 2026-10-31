using OMTS.Application.Common.Messages;
using OMTS.Domain.Enums;

namespace OMTS.Application.Features.WaterQuality.Dtos;

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
