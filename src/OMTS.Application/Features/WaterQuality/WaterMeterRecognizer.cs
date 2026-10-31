using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using OMTS.Application.Common.Interfaces.Ocr;
using OMTS.Application.Features.WaterQuality.Dtos;
using OMTS.Domain.Entities.Cultivation;
using OMTS.Domain.Enums;

namespace OMTS.Application.Features.WaterQuality;

/// <summary>
/// Sends a meter photo to the OCR Service within <c>Ocr:TimeoutSeconds</c> and turns the answer into the
/// draft values of a measurement. A failure or timeout never blocks the user: the outcome is Failed with
/// MSG-WATER-01 and the values stay empty for manual entry. Values outside the valid ranges are treated as not read.
/// </summary>
public sealed class WaterMeterRecognizer(
    IOcrService ocr,
    IOptions<OcrOptions> options,
    TimeProvider timeProvider,
    ILogger<WaterMeterRecognizer> logger)
{
    public async Task<OcrOutcome> RecognizeAsync(Stream photo, string contentType, CancellationToken cancellationToken)
    {
        if (photo.CanSeek)
        {
            photo.Position = 0;
        }

        var timeout = TimeSpan.FromSeconds(Math.Max(1, options.Value.TimeoutSeconds));
        using var timeoutSource = new CancellationTokenSource(timeout, timeProvider);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutSource.Token);
        OcrResult result;
        try
        {
            // WaitAsync also ends the wait for a provider that ignores the token.
            result = await ocr.RecognizeAsync(photo, contentType, linked.Token).WaitAsync(timeout, timeProvider, cancellationToken);
        }
        catch (Exception exception) when (exception is TimeoutException || (exception is OperationCanceledException && !cancellationToken.IsCancellationRequested))
        {
            logger.LogWarning("OCR Service did not answer within {Timeout}", timeout);
            result = OcrResult.Failed("TIMEOUT");
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            logger.LogError(exception, "OCR Service call failed");
            result = OcrResult.Failed("ERROR");
        }

        if (!result.Succeeded)
        {
            logger.LogWarning("OCR Service failed: {ErrorCode}", result.ErrorCode);
            return OcrOutcome.Failed();
        }

        var now = timeProvider.GetUtcNow();
        return new OcrOutcome(
            OcrStatus.Success,
            Field(result.Ph, WaterValueRules.IsValidPh),
            Field(result.Salinity, WaterValueRules.IsValidSalinity),
            Field(result.MeasuredAt, at => WaterValueRules.IsValidMeasuredAt(at, now)),
            null);
    }

    /// <summary>
    /// Stores a successful reading as the first OCR draft. Values the draft already holds (entered by hand)
    /// are kept; the OCR time becomes the measurement time only when nothing was entered.
    /// </summary>
    public static void Apply(WaterMeasurement draft, OcrOutcome outcome)
    {
        if (outcome.Status != OcrStatus.Success)
        {
            return;
        }

        var nothingEntered = draft.Ph is null && draft.Salinity is null;
        draft.OcrPh = outcome.Ph?.Value;
        draft.OcrSalinity = outcome.Salinity?.Value;
        draft.OcrMeasuredAt = outcome.MeasuredAt?.Value;
        draft.Ph ??= outcome.Ph?.Value;
        draft.Salinity ??= outcome.Salinity?.Value;
        if (nothingEntered && outcome.MeasuredAt is { } measuredAt)
        {
            draft.MeasuredAt = measuredAt.Value;
        }

        if (nothingEntered && (outcome.Ph is not null || outcome.Salinity is not null))
        {
            draft.InputSource = MeasurementInputSource.Ocr;
        }
    }

    private OcrFieldView<T>? Field<T>(OcrReading<T>? reading, Func<T, bool> isValid) where T : struct =>
        reading is { } read && isValid(read.Value)
            ? new OcrFieldView<T>(read.Value, Math.Clamp(read.Confidence, 0, 1), read.Confidence < options.Value.UncertainBelow)
            : null;
}
