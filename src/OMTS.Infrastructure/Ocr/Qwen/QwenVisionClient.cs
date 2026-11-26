using System.Net.Http.Headers;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using OMTS.Application.Common.Interfaces.Ocr;

namespace OMTS.Infrastructure.Ocr.Qwen;

/// <summary>
/// Alibaba Cloud Model Studio (Qwen) over the OpenAI-compatible <c>chat/completions</c> endpoint (docs/ocr-qwen.md): one image as a
/// data URL plus the prompt, with the response schema written into the prompt text, answered as one JSON object. It implements
/// <see cref="IGeminiVisionClient"/> so the readers and their normalisers are the same as for Gemini. Every provider problem is a
/// failed result with an <see cref="OcrErrorCodes"/> code, never an exception: 401/403 are NOT_CONFIGURED (logged as an error), 429
/// and 5xx and network errors are UNAVAILABLE, other 4xx are BAD_RESPONSE, the reader's timeout (it covers the whole streamed
/// answer) is TIMEOUT. There is no retry here: when Qwen is the fallback it is itself the retry. The key travels in the
/// <c>Authorization</c> header only. The logs hold the status, finish reason, token counts and milliseconds, never the key, the
/// image or the model text, and no error body is passed on.
/// </summary>
internal sealed class QwenVisionClient(
    HttpClient http, IOptions<OcrProviderOptions> options, TimeProvider clock, ILogger<QwenVisionClient> logger) : IGeminiVisionClient
{
    public async Task<GeminiJsonResult> GenerateJsonAsync(
        GeminiReader reader, ReadOnlyMemory<byte> image, string mimeType, string prompt, JsonElement responseSchema, CancellationToken cancellationToken)
    {
        var started = clock.GetTimestamp();
        var qwen = options.Value.Qwen;
        var settings = qwen.For(reader);
        if (!TryBuildUri(qwen, settings, out var uri))
        {
            logger.LogError("Qwen {Reader} is not configured: an API key, an https BaseUrl and a valid model id are required", reader);
            return GeminiJsonResult.Failure(OcrErrorCodes.NotConfigured, TimeSpan.Zero);
        }

        var timeout = TimeSpan.FromSeconds(Math.Max(0.001, settings.TimeoutSeconds));
        using var timeoutSource = new CancellationTokenSource(timeout, clock);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutSource.Token);
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, uri)
            {
                Content = new ByteArrayContent(BuildBody(image, mimeType, QwenPromptText.Compose(prompt, responseSchema), qwen, settings)),
            };
            request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", qwen.ApiKey);
            // Headers first: a streamed answer is read as it arrives, still under the same timeout.
            using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, linked.Token);
            var status = (int)response.StatusCode;
            if (!response.IsSuccessStatusCode)
            {
                return Refused(reader, settings.Model, status, clock.GetElapsedTime(started));
            }

            QwenCompletion completion;
            if (settings.Stream)
            {
                using var reading = new StreamReader(await response.Content.ReadAsStreamAsync(linked.Token));
                completion = await QwenAnswer.ReadStreamAsync(reading, linked.Token);
            }
            else
            {
                completion = QwenAnswer.ReadBody(await response.Content.ReadAsStringAsync(linked.Token));
            }

            return QwenAnswer.ToResult(completion, status, clock.GetElapsedTime(started), reader, settings.Model, logger);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            logger.LogWarning("Qwen {Reader} did not answer within {Seconds} s", reader, timeout.TotalSeconds);
            return GeminiJsonResult.Failure(OcrErrorCodes.Timeout, clock.GetElapsedTime(started));
        }
        catch (Exception exception) when (exception is HttpRequestException or IOException)
        {
            // The exception message can hold the address; only its type is logged.
            logger.LogWarning("Qwen {Reader} unreachable: {Error}", reader, exception.GetType().Name);
            return GeminiJsonResult.Failure(OcrErrorCodes.Unavailable, clock.GetElapsedTime(started));
        }
    }

    private GeminiJsonResult Refused(GeminiReader reader, string model, int status, TimeSpan elapsed)
    {
        var code = status is 401 or 403 ? OcrErrorCodes.NotConfigured
            : status is 429 or >= 500 ? OcrErrorCodes.Unavailable
            : OcrErrorCodes.BadResponse;
        if (code == OcrErrorCodes.NotConfigured)
        {
            logger.LogError("Qwen {Reader} ({Model}) refused the request with {Status}: check the API key, the model id and the free quota", reader, model, status);
        }
        else
        {
            logger.LogWarning("Qwen {Reader} ({Model}) answered {Status}", reader, model, status);
        }

        return GeminiJsonResult.Failure(code, elapsed);
    }

    /// <summary>The request address: <c>{BaseUrl}chat/completions</c>. False without a key, with a non-https BaseUrl or an odd model id.</summary>
    internal static bool TryBuildUri(QwenOptions qwen, QwenReaderOptions settings, out Uri uri)
    {
        uri = null!;
        if (string.IsNullOrWhiteSpace(qwen.ApiKey) || !QwenOptions.IsValidModelId(settings.Model)
            || !Uri.TryCreate(qwen.BaseUrl.TrimEnd('/') + "/", UriKind.Absolute, out var baseUri) || baseUri.Scheme != Uri.UriSchemeHttps)
        {
            return false;
        }

        uri = new Uri(baseUri, "chat/completions");
        return true;
    }

    /// <summary>
    /// <c>{model, temperature:0, stream, stream_options?, enable_thinking?, response_format?, messages:[{role:user, content:[{type:image_url,
    /// image_url:{url:"data:{mime};base64,..."}}, {type:text, text}]}]}</c>. The image part comes first, as in the manual test of 26/11/2026.
    /// </summary>
    internal static byte[] BuildBody(ReadOnlyMemory<byte> image, string mimeType, string prompt, QwenOptions qwen, QwenReaderOptions settings)
    {
        using var stream = new MemoryStream(image.Length + (image.Length / 3) + 1024);
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            writer.WriteString("model", settings.Model);
            writer.WriteNumber("temperature", 0);
            writer.WriteBoolean("stream", settings.Stream);
            if (settings.Stream)
            {
                writer.WriteStartObject("stream_options");
                writer.WriteBoolean("include_usage", true);
                writer.WriteEndObject();
            }

            if (qwen.DisableThinking)
            {
                writer.WriteBoolean("enable_thinking", false);
            }

            if (qwen.JsonMode)
            {
                writer.WriteStartObject("response_format");
                writer.WriteString("type", "json_object");
                writer.WriteEndObject();
            }

            writer.WriteStartArray("messages");
            writer.WriteStartObject();
            writer.WriteString("role", "user");
            writer.WriteStartArray("content");
            writer.WriteStartObject();
            writer.WriteString("type", "image_url");
            writer.WriteStartObject("image_url");
            writer.WriteString("url", $"data:{mimeType};base64," + Convert.ToBase64String(image.Span));
            writer.WriteEndObject();
            writer.WriteEndObject();
            writer.WriteStartObject();
            writer.WriteString("type", "text");
            writer.WriteString("text", prompt);
            writer.WriteEndObject();
            writer.WriteEndArray();
            writer.WriteEndObject();
            writer.WriteEndArray();
            writer.WriteEndObject();
        }

        return stream.ToArray();
    }
}
