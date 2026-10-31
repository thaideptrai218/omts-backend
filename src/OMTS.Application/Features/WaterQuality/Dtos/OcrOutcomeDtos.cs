using OMTS.Application.Common.Messages;

namespace OMTS.Application.Features.WaterQuality.Dtos;

/// <summary>How the OCR step went: Success (values read), Failed (error or timeout, enter manually), NotCalled (already read or a re-send).</summary>
public enum OcrStatus
{
    Success,
    Failed,
    NotCalled,
}

/// <summary>A value read from the meter photo; <see cref="Uncertain"/> when the confidence is below <c>Ocr:UncertainBelow</c>.</summary>
public sealed record OcrFieldView<T>(T Value, double Confidence, bool Uncertain) where T : struct;

/// <summary>The OCR draft of a recognition; <see cref="Warning"/> is MSG-WATER-01 when the OCR Service failed.</summary>
public sealed record OcrOutcome(
    OcrStatus Status,
    OcrFieldView<decimal>? Ph,
    OcrFieldView<decimal>? Salinity,
    OcrFieldView<DateTimeOffset>? MeasuredAt,
    UserMessage? Warning)
{
    public static readonly OcrOutcome NotCalled = new(OcrStatus.NotCalled, null, null, null, null);

    /// <summary>Nothing was read (WATER-01.2): pH and salinity are typed in by hand.</summary>
    public static OcrOutcome Failed() => new(OcrStatus.Failed, null, null, null, UserMessages.OcrValuesNotRead("pH and salinity"));
}

/// <summary>ENV-01 recognize: the saved Draft (with the photo) and what OCR read.</summary>
public sealed record RecognitionResult(WaterMeasurementDetail Measurement, OcrOutcome Ocr);
