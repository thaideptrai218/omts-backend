using OMTS.Domain.Entities.Cultivation;
using OMTS.Domain.Entities.Farming;

namespace OMTS.Application.Features.WaterQuality.Dtos;

/// <summary>ENV-04 – set the thresholds of a zone (or of every In Use zone); each pair is set or cleared together.</summary>
public sealed record SetWaterThresholdsRequest(decimal? PhMin, decimal? PhMax, decimal? SalinityMin, decimal? SalinityMax);
