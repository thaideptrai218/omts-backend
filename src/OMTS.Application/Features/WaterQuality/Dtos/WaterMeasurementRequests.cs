namespace OMTS.Application.Features.WaterQuality.Dtos;

/// <summary>
/// WATER-01 steps 9–10: the values the user confirmed, saved as Active with the zone thresholds. Every value is
/// required; <see cref="PhotoKey"/> comes from recognize. A re-sent <see cref="ClientOperationId"/> (optional, generated
/// by the server when empty) returns the record already saved.
/// </summary>
/// <param name="MeasuredAt">ISO 8601; without a UTC offset it is Vietnam time (UTC+7), "Z" or "+07:00" is kept as sent.</param>
public sealed record RecordWaterMeasurementRequest(
    Guid? RaftId,
    DateTimeOffset? MeasuredAt,
    decimal? Ph,
    decimal? Salinity,
    decimal? Temperature,
    string? PhotoKey,
    Guid? ClientOperationId = null);

/// <summary>WATER-02 correction of an Active measurement in place, with a reason; every value is required.</summary>
/// <param name="RaftId">The raft it already has, or another raft In Use in the same zone.</param>
/// <param name="MeasuredAt">ISO 8601; without a UTC offset it is Vietnam time (UTC+7), "Z" or "+07:00" is kept as sent.</param>
public sealed record CorrectWaterMeasurementRequest(
    Guid? RaftId,
    DateTimeOffset? MeasuredAt,
    decimal? Ph,
    decimal? Salinity,
    decimal? Temperature,
    string? Reason);

/// <summary>WATER-02.1 void of an Active measurement, with a reason.</summary>
public sealed record VoidWaterMeasurementRequest(string? Reason);
