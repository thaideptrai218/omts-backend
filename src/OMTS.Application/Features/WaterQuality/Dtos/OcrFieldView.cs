using OMTS.Application.Common.Messages;

namespace OMTS.Application.Features.WaterQuality.Dtos;

/// <summary>A value read from the meter photo; <see cref="Uncertain"/> when the confidence is below <c>Ocr:UncertainBelow</c>.</summary>
public sealed record OcrFieldView<T>(T Value, double Confidence, bool Uncertain) where T : struct;
