
namespace OMTS.Application.Common.Models.Ocr;

/// <summary>A value read from the photo with the provider's confidence (0…1).</summary>
public sealed record OcrReading<T>(T Value, double Confidence) where T : struct;
