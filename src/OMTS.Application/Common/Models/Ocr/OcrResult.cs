
namespace OMTS.Application.Common.Models.Ocr;

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
