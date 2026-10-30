using OMTS.Application.Common.Messages;

namespace OMTS.Application.Features.WaterQuality.Dtos;

/// <summary>The OCR draft of a recognition; <see cref="Warning"/> is MSG04 when the OCR Service failed.</summary>
public sealed record OcrOutcome(
    OcrStatus Status,
    OcrFieldView<decimal>? Ph,
    OcrFieldView<decimal>? Salinity,
    OcrFieldView<DateTimeOffset>? MeasuredAt,
    UserMessage? Warning)
{
    public static readonly OcrOutcome NotCalled = new(OcrStatus.NotCalled, null, null, null, null);

    public static OcrOutcome Failed() => new(OcrStatus.Failed, null, null, null, UserMessages.ServiceNotResponding("OCR"));
}
