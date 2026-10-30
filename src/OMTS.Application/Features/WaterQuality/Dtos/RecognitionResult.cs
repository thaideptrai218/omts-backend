using OMTS.Application.Common.Messages;

namespace OMTS.Application.Features.WaterQuality.Dtos;

/// <summary>ENV-01 recognize: the saved Draft (with the photo) and what OCR read.</summary>
public sealed record RecognitionResult(WaterMeasurementDetail Measurement, OcrOutcome Ocr);
