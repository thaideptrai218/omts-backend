using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using OMTS.Application.Common.Interfaces.Ocr;

namespace OMTS.Infrastructure.Ocr.Gemini;

/// <summary>
/// Reads a successful (HTTP 200) <c>generateContent</c> body: <c>candidates[0].finishReason</c> must be STOP, the text is the
/// concatenation of the non-thought parts of <c>candidates[0].content.parts[].text</c>, and it must be one JSON object (a
/// Markdown fence around it is not accepted: the schema mode does not produce one). Anything else is BAD_RESPONSE. The model text
/// is parsed and dropped, never logged.
/// </summary>
internal static class GeminiAnswer
{
    public static GeminiJsonResult Read(string body, int status, TimeSpan elapsed, GeminiReader reader, string model, ILogger logger)
    {
        try
        {
            using var document = JsonDocument.Parse(body);
            var root = document.RootElement;
            var usage = UsageOf(root);
            var candidate = root.ValueKind == JsonValueKind.Object && root.TryGetProperty("candidates", out var candidates)
                && candidates.ValueKind == JsonValueKind.Array && candidates.GetArrayLength() > 0
                    ? candidates[0]
                    : (JsonElement?)null;
            var finishReason = candidate is { } c && c.TryGetProperty("finishReason", out var reason) ? reason.GetString() : null;
            // blockReason (promptFeedback) when there is no candidate at all.
            var blockReason = root.TryGetProperty("promptFeedback", out var feedback) && feedback.TryGetProperty("blockReason", out var block)
                ? block.GetString()
                : null;
            logger.LogInformation(
                "Gemini {Reader} ({Model}): HTTP {Status}, finishReason {FinishReason}, blockReason {BlockReason}, tokens prompt {Prompt} output {Output} thoughts {Thoughts}, {Ms} ms",
                reader, model, status, finishReason, blockReason, usage.PromptTokens, usage.OutputTokens, usage.ThoughtTokens, (int)elapsed.TotalMilliseconds);

            if (candidate is not { } found || finishReason != "STOP")
            {
                return GeminiJsonResult.Failure(OcrErrorCodes.BadResponse, elapsed, usage);
            }

            var text = new StringBuilder();
            if (found.TryGetProperty("content", out var content) && content.TryGetProperty("parts", out var parts) && parts.ValueKind == JsonValueKind.Array)
            {
                foreach (var part in parts.EnumerateArray())
                {
                    var isThought = part.TryGetProperty("thought", out var thought) && thought.ValueKind == JsonValueKind.True;
                    if (!isThought && part.TryGetProperty("text", out var partText) && partText.ValueKind == JsonValueKind.String)
                    {
                        text.Append(partText.GetString());
                    }
                }
            }

            using var answer = JsonDocument.Parse(text.ToString());
            return answer.RootElement.ValueKind == JsonValueKind.Object
                ? new GeminiJsonResult(true, answer.RootElement.Clone(), null, usage, elapsed)
                : GeminiJsonResult.Failure(OcrErrorCodes.BadResponse, elapsed, usage);
        }
        catch (Exception exception) when (exception is JsonException or InvalidOperationException)
        {
            logger.LogWarning("Gemini {Reader} ({Model}): the answer is not the JSON asked for", reader, model);
            return GeminiJsonResult.Failure(OcrErrorCodes.BadResponse, elapsed);
        }
    }

    private static GeminiUsage UsageOf(JsonElement root) =>
        root.ValueKind == JsonValueKind.Object && root.TryGetProperty("usageMetadata", out var usage) && usage.ValueKind == JsonValueKind.Object
            ? new GeminiUsage(Count(usage, "promptTokenCount"), Count(usage, "candidatesTokenCount"), Count(usage, "thoughtsTokenCount"))
            : new GeminiUsage(null, null, null);

    private static int? Count(JsonElement usage, string name) =>
        usage.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var count) ? count : null;
}
