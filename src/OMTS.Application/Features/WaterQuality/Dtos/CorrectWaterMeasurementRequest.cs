
namespace OMTS.Application.Features.WaterQuality.Dtos;

/// <summary>ENV-02 correction of the latest Confirmed version; <see cref="RecordVersion"/> is the version being edited.</summary>
/// <param name="MeasuredAt">ISO 8601; without a UTC offset it is Vietnam time (UTC+7), "Z" or "+07:00" is kept as sent.</param>
public sealed record CorrectWaterMeasurementRequest(
    DateTimeOffset? MeasuredAt,
    decimal? Ph,
    decimal? Salinity,
    string? Reason,
    int? RecordVersion);
