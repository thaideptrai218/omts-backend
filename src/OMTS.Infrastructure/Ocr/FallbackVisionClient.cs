using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using OMTS.Application.Common.Interfaces.Ocr;

namespace OMTS.Infrastructure.Ocr;

/// <summary>
/// Reads a photo with the primary provider and, when that read fails, once more with the fallback (<c>Ocr:Fallback</c>). The caller sees
/// one <see cref="GeminiJsonResult"/>: which provider produced it is only in the logs, not in the API contract, and the readers
/// apply the same normalisation to it either way.
/// <list type="bullet">
/// <item>The fallback is tried after UNAVAILABLE (429, 5xx, network), NOT_CONFIGURED (key or model refused), TIMEOUT and BAD_RESPONSE
/// (not the JSON asked for, an early stop, an image the provider cannot process): another model may still read the photo. Never after the
/// caller's own cancellation.</item>
/// <item>Both providers share one time budget per read (<see cref="OcrProviderOptions.ReadBudget"/>): the fallback gets what the primary
/// left, never more than its own reader timeout, and is skipped when less than <see cref="MinimumFallbackTime"/> is left.</item>
/// <item>When both fail the primary's code is the result: it is the provider the deployment is set up around, so "try later" stays "try later"
/// even when the fallback key is wrong. The fallback's own code is in the log.</item>
/// </list>
/// </summary>
internal sealed class FallbackVisionClient(
    IGeminiVisionClient primary, string primaryName, IGeminiVisionClient fallback, string fallbackName,
    IOptions<OcrProviderOptions> options, TimeProvider clock, ILogger<FallbackVisionClient> logger) : IGeminiVisionClient
{
    /// <summary>A fallback with less time than this left is not started: no reader answers a photo in that.</summary>
    public static readonly TimeSpan MinimumFallbackTime = TimeSpan.FromSeconds(10);

    public async Task<GeminiJsonResult> GenerateJsonAsync(
        GeminiReader reader, ReadOnlyMemory<byte> image, string mimeType, string prompt, JsonElement responseSchema, CancellationToken cancellationToken)
    {
        var started = clock.GetTimestamp();
        var budget = options.Value.ReadBudget(reader);
        var first = await primary.GenerateJsonAsync(reader, image, mimeType, prompt, responseSchema, cancellationToken);
        if (first.Ok)
        {
            logger.LogInformation("OCR {Reader}: read by {Provider}", reader, primaryName);
            return first;
        }

        var code = first.ErrorCode ?? OcrErrorCodes.BadResponse;
        if (!FallsBack(code) || cancellationToken.IsCancellationRequested)
        {
            return first;
        }

        var remaining = budget - clock.GetElapsedTime(started);
        if (remaining < MinimumFallbackTime)
        {
            logger.LogWarning(
                "OCR {Reader}: {Primary} ended {Code} and only {Seconds:0} s of the {Budget:0} s budget is left; {Fallback} is not tried",
                reader, primaryName, code, Math.Max(0, remaining.TotalSeconds), budget.TotalSeconds, fallbackName);
            return first;
        }

        logger.LogWarning("OCR {Reader}: {Primary} ended {Code}; retrying the same photo once with {Fallback} ({Seconds:0} s left)", reader, primaryName, code, fallbackName, remaining.TotalSeconds);
        GeminiJsonResult second;
        try
        {
            using var budgetEnd = new CancellationTokenSource(remaining, clock);
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, budgetEnd.Token);
            second = await fallback.GenerateJsonAsync(reader, image, mimeType, prompt, responseSchema, linked.Token);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            // The budget ran out before the fallback's own timeout did.
            logger.LogWarning("OCR {Reader}: {Fallback} did not answer within the remaining budget", reader, fallbackName);
            return first;
        }

        var elapsed = clock.GetElapsedTime(started);
        if (second.Ok)
        {
            logger.LogInformation("OCR {Reader}: read by fallback {Provider} after {Primary} ended {Code}, {Ms} ms in all", reader, fallbackName, primaryName, code, (int)elapsed.TotalMilliseconds);
            return second with { Elapsed = elapsed };
        }

        logger.LogWarning("OCR {Reader}: {Fallback} ended {FallbackCode} too; the result is {Primary}'s {Code}", reader, fallbackName, second.ErrorCode, primaryName, code);
        return first with { Elapsed = elapsed };
    }

    /// <summary>The failure codes after which the fallback is tried.</summary>
    internal static bool FallsBack(string errorCode) =>
        errorCode is OcrErrorCodes.Unavailable or OcrErrorCodes.NotConfigured or OcrErrorCodes.Timeout or OcrErrorCodes.BadResponse;
}
