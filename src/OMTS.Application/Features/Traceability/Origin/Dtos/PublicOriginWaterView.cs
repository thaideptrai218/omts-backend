namespace OMTS.Application.Features.Traceability.Origin.Dtos;

/// <summary>The water of the zone while the lot grew: the number of measurements and their averages, null when there is none.</summary>
public sealed record PublicOriginWaterView(int MeasurementCount, decimal? AverageSalinity, decimal? AverageTemperature, decimal? AveragePh);
