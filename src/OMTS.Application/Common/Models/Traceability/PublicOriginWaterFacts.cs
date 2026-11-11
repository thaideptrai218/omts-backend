namespace OMTS.Application.Common.Models.Traceability;

/// <summary>The Active water measurements of the lot's zone from the cycle start to the harvest date; the averages are null when there is none.</summary>
public sealed record PublicOriginWaterFacts(int Count, decimal? AverageSalinity, decimal? AverageTemperature, decimal? AveragePh);
