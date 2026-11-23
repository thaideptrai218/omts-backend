using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using OMTS.Application.Common.Interfaces.Ocr;

namespace OMTS.Infrastructure.Ocr.Paddle;

/// <summary>
/// OCR Service adapter for the PaddleOCR sidecar (tools/ocr-service): sends the meter photo, and the display crop when the
/// user framed one, then turns the lines read into suggestions with <see cref="MeterReadingParser"/>. The sidecar runs in
/// the compose network, so the photo never leaves the host. Every sidecar error, bad answer or HTTP timeout is a failed
/// result (manual entry, MSG-WATER-01), never an exception; the caller's cancellation still cancels.
/// </summary>
internal sealed class PaddleOcrService(HttpClient http, IOptions<OcrProviderOptions> options, ILogger<PaddleOcrService> logger) : IOcrService
{
    public const string UnavailableError = "UNAVAILABLE";
    public const string BadResponseError = "BAD_RESPONSE";
    public const string TimeoutError = "TIMEOUT";

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public async Task<OcrResult> RecognizeAsync(Stream photo, string contentType, OcrCrop? crop, CancellationToken cancellationToken)
    {
        var path = crop is null ? "recognize" : $"recognize?crop={Uri.EscapeDataString(crop.ToString())}";
        // Not disposed here: disposing StreamContent would close the photo stream, which belongs to the caller.
        var content = new StreamContent(photo);
        content.Headers.ContentType = MediaTypeHeaderValue.Parse(contentType);
        try
        {
            using var response = await http.PostAsync(path, content, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                logger.LogWarning("OCR sidecar answered {Status}", (int)response.StatusCode);
                return OcrResult.Failed(UnavailableError);
            }

            var answer = await response.Content.ReadFromJsonAsync<SidecarAnswer>(Json, cancellationToken);
            if (answer?.Lines is null)
            {
                return OcrResult.Failed(BadResponseError);
            }

            var lines = answer.Lines
                .Where(l => l.Text is not null && l.Box is { Length: 4 })
                .Select(l => new OcrLine(l.Text!, l.Score, l.Box![0], l.Box[1], l.Box[2], l.Box[3]))
                .ToList();
            var (ph, salinity, temperature) = MeterReadingParser.Parse(lines, options.Value.Paddle.Profile());
            // Counts only: the lines can hold whatever was in the photo.
            logger.LogInformation("OCR sidecar read {Lines} lines in {Ms} ms (crop {Crop}): pH {Ph}, salinity {Salinity}, temperature {Temperature}",
                lines.Count, answer.Ms, crop is not null, ph is not null, salinity is not null, temperature is not null);
            return OcrResult.Read(ph, salinity, temperature, measuredAt: null);
        }
        catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            logger.LogWarning("OCR sidecar did not answer within {Seconds} s", http.Timeout.TotalSeconds);
            return OcrResult.Failed(TimeoutError);
        }
        catch (HttpRequestException exception)
        {
            logger.LogWarning("OCR sidecar unreachable: {Error}", exception.Message);
            return OcrResult.Failed(UnavailableError);
        }
        // FormatException/OverflowException: a backstop for text the parser cannot read; the OCR text is never logged.
        catch (Exception exception) when (exception is JsonException or NotSupportedException or FormatException or OverflowException)
        {
            logger.LogWarning("OCR sidecar answer not understood: {Error}", exception.GetType().Name);
            return OcrResult.Failed(BadResponseError);
        }
    }

    private sealed record SidecarAnswer(List<SidecarLine>? Lines, int Ms);

    private sealed record SidecarLine(string? Text, double Score, int[]? Box);
}
