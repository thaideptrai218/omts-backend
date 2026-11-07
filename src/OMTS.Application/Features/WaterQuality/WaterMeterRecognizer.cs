using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using OMTS.Application.Common.Interfaces.Ocr;
using OMTS.Application.Common.Messages;
using OMTS.Application.Features.WaterQuality.Dtos;

namespace OMTS.Application.Features.WaterQuality;

/// <summary>
/// Sends a meter photo to the OCR Service within <c>Ocr:TimeoutSeconds</c> and turns the answer into suggestions. A
/// failure or timeout never blocks the user: the outcome is Failed. Values outside the valid ranges are treated as not
/// read, and every indicator not read is named by MSG-WATER-01 (WATER-01.2).
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
        var ph = Field(result.Ph, WaterValueRules.IsValidPh);
        var salinity = Field(result.Salinity, WaterValueRules.IsValidSalinity);
        var temperature = Field(result.Temperature, WaterValueRules.IsValidTemperature);
        var unread = new List<(string Code, string Name)>();
        AddWhenUnread(ph, WaterValueOutside.Ph, WaterIndicators.PhName, unread);
        AddWhenUnread(salinity, WaterValueOutside.Salinity, WaterIndicators.SalinityName, unread);
        AddWhenUnread(temperature, WaterValueOutside.Temperature, WaterIndicators.TemperatureName, unread);
        return new OcrOutcome(
            OcrStatus.Success,
            ph,
            salinity,
            temperature,
            Field(result.MeasuredAt, at => WaterValueRules.IsValidMeasuredAt(at, now)),
            unread.Select(u => u.Code).ToList(),
            unread.Count == 0 ? null : UserMessages.OcrValuesNotRead(WaterIndicators.Join(unread.Select(u => u.Name).ToList())));
    }

    private static void AddWhenUnread(OcrFieldView<decimal>? field, string code, string name, List<(string, string)> unread)
    {
        if (field is null)
        {
            unread.Add((code, name));
        }
    }

    private OcrFieldView<T>? Field<T>(OcrReading<T>? reading, Func<T, bool> isValid) where T : struct =>
        reading is { } read && isValid(read.Value)
            ? new OcrFieldView<T>(read.Value, Math.Clamp(read.Confidence, 0, 1), read.Confidence < options.Value.UncertainBelow)
            : null;
}
