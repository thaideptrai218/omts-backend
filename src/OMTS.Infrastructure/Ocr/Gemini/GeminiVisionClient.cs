using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using OMTS.Application.Common.Interfaces.Ocr;

namespace OMTS.Infrastructure.Ocr.Gemini;

/// <summary>
/// Gemini <c>generateContent</c> over REST (docs/ocr-gemini.md): one inline image plus the prompt, a JSON answer shaped by the
/// response schema. Every provider problem is a failed result with an <see cref="OcrErrorCodes"/> code, never an exception: 429 and
/// 503 are UNAVAILABLE (one retry, only while at least 40 % of the timeout is left), the timeout is TIMEOUT, an answer that is not
/// the JSON asked for or a model that stopped early (safety, token limit) is BAD_RESPONSE, 401/403/404 are NOT_CONFIGURED
/// (logged as an error: the key or the model is wrong), 400 (Gemini answers it for an image it cannot process) is BAD_RESPONSE, other 5xx and network errors are UNAVAILABLE. The key travels in the
/// <c>x-goog-api-key</c> header only. The logs hold the status, finish reason, token counts and milliseconds, never the key, the
/// image or the model text, and no error body is passed on.
/// </summary>
internal sealed partial class GeminiVisionClient(
    HttpClient http, IOptions<OcrProviderOptions> options, TimeProvider clock, ILogger<GeminiVisionClient> logger) : IGeminiVisionClient
{
    private const double RetryMinimumTimeLeft = 0.4;

    public async Task<GeminiJsonResult> GenerateJsonAsync(
        GeminiReader reader, ReadOnlyMemory<byte> image, string mimeType, string prompt, JsonElement responseSchema, CancellationToken cancellationToken)
    {
        var started = clock.GetTimestamp();
        var gemini = options.Value.Gemini;
        var settings = gemini.For(reader);
        if (!TryBuildUri(gemini, settings, out var uri))
        {
            logger.LogError("Gemini {Reader} is not configured: an API key, an https BaseUrl and a valid model id are required", reader);
            return GeminiJsonResult.Failure(OcrErrorCodes.NotConfigured, TimeSpan.Zero);
        }

        var timeout = TimeSpan.FromSeconds(Math.Max(0.001, settings.TimeoutSeconds));
        using var timeoutSource = new CancellationTokenSource(timeout, clock);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutSource.Token);
        var body = BuildBody(image, mimeType, prompt, responseSchema, settings);
        try
        {
            for (var attempt = 1; ; attempt++)
            {
                using var request = new HttpRequestMessage(HttpMethod.Post, uri) { Content = new ByteArrayContent(body) };
                request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
                request.Headers.Add("x-goog-api-key", gemini.ApiKey);
                using var response = await http.SendAsync(request, linked.Token);
                var status = (int)response.StatusCode;
                if (response.IsSuccessStatusCode)
                {
                    var text = await response.Content.ReadAsStringAsync(linked.Token);
                    return GeminiAnswer.Read(text, status, clock.GetElapsedTime(started), reader, settings.Model, logger);
                }

                if (status is 429 or 503)
                {
                    var timeLeft = timeout - clock.GetElapsedTime(started);
                    if (attempt == 1 && timeLeft >= timeout * RetryMinimumTimeLeft)
                    {
                        logger.LogWarning("Gemini {Reader} answered {Status}; retrying once", reader, status);
                        await Task.Delay(TimeSpan.FromMilliseconds(Math.Max(0, gemini.RetryPauseMs)), clock, linked.Token);
                        continue;
                    }

                    logger.LogWarning("Gemini {Reader} answered {Status}", reader, status);
                    return GeminiJsonResult.Failure(OcrErrorCodes.Unavailable, clock.GetElapsedTime(started));
                }

                var code = status is 401 or 403 or 404 ? OcrErrorCodes.NotConfigured
                    : status >= 500 ? OcrErrorCodes.Unavailable
                    : OcrErrorCodes.BadResponse;
                if (code == OcrErrorCodes.NotConfigured)
                {
                    logger.LogError("Gemini {Reader} ({Model}) refused the request with {Status}: check the API key, the model id and the quota", reader, settings.Model, status);
                }
                else
                {
                    logger.LogWarning("Gemini {Reader} answered {Status}", reader, status);
                }

                return GeminiJsonResult.Failure(code, clock.GetElapsedTime(started));
            }
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            logger.LogWarning("Gemini {Reader} did not answer within {Seconds} s", reader, timeout.TotalSeconds);
            return GeminiJsonResult.Failure(OcrErrorCodes.Timeout, clock.GetElapsedTime(started));
        }
        catch (HttpRequestException exception)
        {
            // The exception message can hold the address; only its type and status are logged.
            logger.LogWarning("Gemini {Reader} unreachable: {Error} {Status}", reader, exception.GetType().Name, exception.StatusCode);
            return GeminiJsonResult.Failure(OcrErrorCodes.Unavailable, clock.GetElapsedTime(started));
        }
    }

    /// <summary>The request address: <c>{BaseUrl}models/{model}:generateContent</c>. False without a key, with a non-https BaseUrl or an odd model id.</summary>
    private static bool TryBuildUri(GeminiOptions gemini, GeminiReaderOptions settings, out Uri uri)
    {
        uri = null!;
        if (string.IsNullOrWhiteSpace(gemini.ApiKey) || !ModelId().IsMatch(settings.Model) || settings.Model.Contains("..")
            || !Uri.TryCreate(gemini.BaseUrl.TrimEnd('/') + "/", UriKind.Absolute, out var baseUri) || baseUri.Scheme != Uri.UriSchemeHttps)
        {
            return false;
        }

        uri = new Uri(baseUri, $"models/{settings.Model}:generateContent");
        return true;
    }

    /// <summary>
    /// <c>{contents:[{role:user, parts:[{inlineData:{mimeType,data}}, {text}]}], generationConfig:{temperature:0, responseMimeType,
    /// responseSchema, thinkingConfig?}}</c>, the same body the google-genai SDK sends. The image part comes first.
    /// </summary>
    private static byte[] BuildBody(ReadOnlyMemory<byte> image, string mimeType, string prompt, JsonElement schema, GeminiReaderOptions settings)
    {
        using var stream = new MemoryStream(image.Length + (image.Length / 3) + 1024);
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            writer.WriteStartArray("contents");
            writer.WriteStartObject();
            writer.WriteString("role", "user");
            writer.WriteStartArray("parts");
            writer.WriteStartObject();
            writer.WriteStartObject("inlineData");
            writer.WriteString("mimeType", mimeType);
            writer.WriteBase64String("data", image.Span);
            writer.WriteEndObject();
            writer.WriteEndObject();
            writer.WriteStartObject();
            writer.WriteString("text", prompt);
            writer.WriteEndObject();
            writer.WriteEndArray();
            writer.WriteEndObject();
            writer.WriteEndArray();
            writer.WriteStartObject("generationConfig");
            writer.WriteNumber("temperature", 0);
            writer.WriteString("responseMimeType", "application/json");
            writer.WritePropertyName("responseSchema");
            schema.WriteTo(writer);
            if (settings.ThinkingBudget is { } budget)
            {
                writer.WriteStartObject("thinkingConfig");
                writer.WriteNumber("thinkingBudget", budget);
                writer.WriteEndObject();
            }

            writer.WriteEndObject();
            writer.WriteEndObject();
        }

        return stream.ToArray();
    }

    [GeneratedRegex(@"^[A-Za-z0-9._-]+$")]
    private static partial Regex ModelId();
}
