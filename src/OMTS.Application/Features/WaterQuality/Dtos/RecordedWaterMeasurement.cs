using OMTS.Application.Common.Messages;
using OMTS.Domain.Enums;

namespace OMTS.Application.Features.WaterQuality.Dtos;

/// <summary>Result of ENV-01 create: <see cref="Replayed"/> when the client operation was already saved (answered 200, not 201).</summary>
public sealed record RecordedWaterMeasurement(WaterMeasurementDetail Measurement, bool Replayed);
