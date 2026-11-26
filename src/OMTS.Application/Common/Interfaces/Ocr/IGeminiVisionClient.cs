using System.Text.Json;

namespace OMTS.Application.Common.Interfaces.Ocr;

/// <summary>The readers of photos that go to Gemini; each has its own model, timeout and thinking budget (<c>Ocr:Gemini:Readers:*</c>).</summary>
public enum GeminiReader
{
    /// <summary>The water meter display (WATER-01), read synchronously.</summary>
    Meter,

    /// <summary>The corner-coordinate tables of the sea-area licence map (asynchronous scan).</summary>
    ZoneMap,

    /// <summary>The raft form (asynchronous scan).</summary>
    RaftForm,
}

/// <summary>The failure codes of every reading of a photo (<see cref="GeminiJsonResult.ErrorCode"/>, scan <c>errorCode</c>, <see cref="OcrResult.ErrorCode"/>).</summary>
public static class OcrErrorCodes
{
    /// <summary>The provider is overloaded or unreachable (HTTP 429, 5xx, network): try again later.</summary>
    public const string Unavailable = "UNAVAILABLE";

    public const string Timeout = "TIMEOUT";

    /// <summary>The answer is not the JSON asked for, the model stopped for a reason such as safety or the token limit, or Gemini refused the image itself (HTTP 400).</summary>
    public const string BadResponse = "BAD_RESPONSE";

    /// <summary>No provider, or the provider refuses the key or the model (HTTP 401/403/404): a deployment mistake.</summary>
    public const string NotConfigured = "NOT_CONFIGURED";
}

/// <summary>Token counts the provider reported; null when it left a count out.</summary>
public sealed record GeminiUsage(int? PromptTokens, int? OutputTokens, int? ThoughtTokens);

/// <summary>
/// What one call answered: the JSON object the model wrote (<see cref="Json"/>), or an <see cref="OcrErrorCodes"/> code. The
/// model text is never kept beyond the parsed JSON.
/// </summary>
public sealed record GeminiJsonResult(bool Ok, JsonElement? Json, string? ErrorCode, GeminiUsage? Usage, TimeSpan Elapsed)
{
    public static GeminiJsonResult Failure(string errorCode, TimeSpan elapsed, GeminiUsage? usage = null) => new(false, null, errorCode, usage, elapsed);
}

/// <summary>
/// A vision model call with one image and a JSON answer: Gemini <c>generateContent</c>, or Qwen chat completions (which takes the schema
/// as prompt text) when Qwen is the provider or the fallback (docs/ocr-qwen.md). Never throws for a provider problem (a failed result
/// instead); only the caller's own cancellation cancels. The timeout, model and thinking budget come from the
/// reader's settings.
/// </summary>
public interface IGeminiVisionClient
{
    /// <param name="reader">Picks the model, timeout and thinking budget.</param>
    /// <param name="image">The photo as sent by the user (JPEG or PNG); the server never resizes it.</param>
    /// <param name="mimeType">image/jpeg or image/png.</param>
    /// <param name="prompt">The instruction; it copies what is written, it never states a rule the numbers obey.</param>
    /// <param name="responseSchema">The Gemini response schema (upper-case types, see docs/ocr-gemini.md).</param>
    Task<GeminiJsonResult> GenerateJsonAsync(
        GeminiReader reader, ReadOnlyMemory<byte> image, string mimeType, string prompt, JsonElement responseSchema, CancellationToken cancellationToken);
}
