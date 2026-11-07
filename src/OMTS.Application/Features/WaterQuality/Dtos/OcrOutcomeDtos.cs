using OMTS.Application.Common.Messages;

namespace OMTS.Application.Features.WaterQuality.Dtos;

/// <summary>How the OCR step went: Success (the OCR Service answered), Failed (error or timeout, enter the values by hand).</summary>
public enum OcrStatus
{
    Success,
    Failed,
}

/// <summary>A value read from the meter photo; <see cref="Uncertain"/> when the confidence is below <c>Ocr:UncertainBelow</c>.</summary>
public sealed record OcrFieldView<T>(T Value, double Confidence, bool Uncertain) where T : struct;

/// <summary>
/// The suggestions of a recognition, never stored (BR-WATER-02). <see cref="Unread"/> names the indicators left empty
/// (<see cref="Common.Messages.WaterValueOutside"/> codes) and <see cref="Warning"/> is MSG-WATER-01 naming them
/// (WATER-01.2): a value outside its allowed range counts as not read.
/// </summary>
public sealed record OcrOutcome(
    OcrStatus Status,
    OcrFieldView<decimal>? Ph,
    OcrFieldView<decimal>? Salinity,
    OcrFieldView<decimal>? Temperature,
    OcrFieldView<DateTimeOffset>? MeasuredAt,
    IReadOnlyList<string> Unread,
    UserMessage? Warning)
{
    /// <summary>Nothing was read (WATER-01.2): every value is typed in by hand.</summary>
    public static OcrOutcome Failed() => new(
        OcrStatus.Failed, null, null, null, null,
        [WaterValueOutside.Ph, WaterValueOutside.Salinity, WaterValueOutside.Temperature],
        UserMessages.OcrValuesNotRead(WaterIndicators.Join(WaterIndicators.All)));
}

/// <summary>
/// WATER-01 steps 5–6: the stored photo and what the OCR Service suggests; nothing is saved until the record request.
/// </summary>
/// <param name="PhotoKey">Send it back when recording; only the account that uploaded the photo can use it.</param>
/// <param name="MeasuredAt">The current time, the suggested measurement time (step 6).</param>
/// <param name="ValuesOutside">The values read that are outside the zone thresholds (empty when none).</param>
/// <param name="ThresholdWarning">MSG-WATER-02 for <see cref="ValuesOutside"/>; the client checks again after an edit.</param>
public sealed record RecognitionResult(
    string PhotoKey,
    DateTimeOffset MeasuredAt,
    OcrOutcome Ocr,
    IReadOnlyList<WaterValueOutside> ValuesOutside,
    UserMessage? ThresholdWarning);
