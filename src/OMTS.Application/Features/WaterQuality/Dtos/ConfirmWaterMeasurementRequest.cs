
namespace OMTS.Application.Features.WaterQuality.Dtos;

/// <summary>ENV-01 Draft → Confirmed with the final values; an omitted value keeps the draft's value.</summary>
/// <param name="MeasuredAt">ISO 8601; without a UTC offset it is Vietnam time (UTC+7), "Z" or "+07:00" is kept as sent.</param>
public sealed record ConfirmWaterMeasurementRequest(DateTimeOffset? MeasuredAt, decimal? Ph, decimal? Salinity);
