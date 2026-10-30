using OMTS.Application.Common.Messages;
using OMTS.Domain.Enums;

namespace OMTS.Application.Features.WaterQuality.Dtos;

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
