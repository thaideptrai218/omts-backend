
namespace OMTS.Application.Features.WaterQuality.Dtos;

/// <summary>ENV-02 void of a Draft or of the latest Confirmed version.</summary>
public sealed record VoidWaterMeasurementRequest(string? Reason, int? RecordVersion);
