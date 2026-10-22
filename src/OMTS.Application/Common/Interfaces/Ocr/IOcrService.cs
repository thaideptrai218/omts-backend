namespace OMTS.Application.Common.Interfaces.Ocr;

/// <summary>
/// OCR Service adapter (ENV-01): reads pH, salinity and the measurement time from a photo of the water
/// meter. Only the meter photo is sent. The result is only a draft until a person confirms it (NFR-05).
/// Provider failures are returned as a failed <see cref="OcrResult"/>, never thrown; the caller enforces
/// the timeout through the cancellation token.
/// </summary>
public interface IOcrService
{
    Task<OcrResult> RecognizeAsync(Stream photo, string contentType, CancellationToken cancellationToken);
}

/// <summary>A value read from the photo with the provider's confidence (0…1).</summary>
public sealed record OcrReading<T>(T Value, double Confidence) where T : struct;

/// <summary>What the OCR Service read; every field is optional, <see cref="ErrorCode"/> is set on failure.</summary>
public sealed record OcrResult(
    bool Succeeded,
    OcrReading<decimal>? Ph,
    OcrReading<decimal>? Salinity,
    OcrReading<DateTimeOffset>? MeasuredAt,
    string? ErrorCode)
{
    public static OcrResult Read(OcrReading<decimal>? ph, OcrReading<decimal>? salinity, OcrReading<DateTimeOffset>? measuredAt) =>
        new(true, ph, salinity, measuredAt, null);

    public static OcrResult Failed(string errorCode) => new(false, null, null, null, errorCode);
}
