using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using OMTS.Application.Common.Interfaces.Ocr;

namespace OMTS.Infrastructure.Ocr.Qwen;

/// <summary>What a chat completion said, streamed or not: the text of the answer (never the reasoning), why it stopped, the tokens, and an error the stream carried.</summary>
internal sealed record QwenCompletion(string Text, string? FinishReason, GeminiUsage Usage, bool Complete, string? ProviderError);

/// <summary>
/// Reads the answer of the OpenAI-compatible chat completions endpoint. The streamed form is server-sent events: <c>data: {chunk}</c>
/// lines with <c>choices[0].delta.content</c> (and <c>reasoning_content</c>, which is skipped), a last chunk with <c>usage</c>, and
/// <c>data: [DONE]</c>. The plain form is one body with <c>choices[0].message.content</c>. The answer must be one JSON object (a
/// Markdown fence around it is removed, the omni models sometimes add one). The model text is parsed and dropped, never logged.
/// </summary>
internal static partial class QwenAnswer
{
    private static readonly GeminiUsage NoUsage = new(null, null, null);

    public static async Task<QwenCompletion> ReadStreamAsync(TextReader reader, CancellationToken cancellationToken)
    {
        var state = new Accumulator();
        while (await reader.ReadLineAsync(cancellationToken) is { } line)
        {
            // Comment lines (": keep-alive") and the "event:" / "id:" fields carry no data.
            if (!line.StartsWith("data:", StringComparison.Ordinal))
            {
                continue;
            }

            var data = line[5..].Trim();
            if (data.Length == 0)
            {
                continue;
            }

            if (data == "[DONE]")
            {
                state.Complete = true;
                break;
            }

            state.Apply(data, "delta");
        }

        return state.ToCompletion();
    }

    public static QwenCompletion ReadBody(string body)
    {
        var state = new Accumulator();
        state.Apply(body, "message");
        state.Complete = true;
        return state.ToCompletion();
    }

    public static GeminiJsonResult ToResult(QwenCompletion completion, int status, TimeSpan elapsed, GeminiReader reader, string model, ILogger logger)
    {
        var usage = completion.Usage;
        logger.LogInformation(
            "Qwen {Reader} ({Model}): HTTP {Status}, finishReason {FinishReason}, tokens prompt {Prompt} output {Output} reasoning {Reasoning}, {Ms} ms",
            reader, model, status, completion.FinishReason, usage.PromptTokens, usage.OutputTokens, usage.ThoughtTokens, (int)elapsed.TotalMilliseconds);

        if (completion.ProviderError is { } error)
        {
            // Only the provider's error code (an identifier such as "Throttling.RateQuota"), never its message.
            logger.LogWarning("Qwen {Reader} ({Model}): the stream carried the error code {Code}", reader, model, error);
            return GeminiJsonResult.Failure(ErrorCodeFor(error), elapsed, usage);
        }

        // A missing finish reason is fine when the stream ended with [DONE]; a cut-off stream (no [DONE]) is not.
        var stopped = completion.FinishReason is "stop" || (completion.FinishReason is null && completion.Complete);
        if (!stopped)
        {
            return GeminiJsonResult.Failure(OcrErrorCodes.BadResponse, elapsed, usage);
        }

        try
        {
            using var answer = JsonDocument.Parse(StripFence(completion.Text));
            return answer.RootElement.ValueKind == JsonValueKind.Object
                ? new GeminiJsonResult(true, answer.RootElement.Clone(), null, usage, elapsed)
                : GeminiJsonResult.Failure(OcrErrorCodes.BadResponse, elapsed, usage);
        }
        catch (JsonException)
        {
            logger.LogWarning("Qwen {Reader} ({Model}): the answer is not the JSON asked for", reader, model);
            return GeminiJsonResult.Failure(OcrErrorCodes.BadResponse, elapsed, usage);
        }
    }

    /// <summary>The text without a surrounding Markdown fence (three backticks, an optional language, the JSON, three backticks); anything else is returned trimmed and judged by the JSON parser.</summary>
    internal static string StripFence(string text)
    {
        var match = Fence().Match(text);
        return match.Success ? match.Groups[1].Value : text.Trim();
    }

    /// <summary>An error code the stream carried: rate and quota problems are the provider being busy, a refused key is a deployment mistake.</summary>
    private static string ErrorCodeFor(string code)
    {
        var lower = code.ToLowerInvariant();
        if (lower.Contains("rate") || lower.Contains("limit") || lower.Contains("throttl") || lower.Contains("quota") || lower.Contains("overload")
            || lower.Contains("busy") || lower.Contains("unavailable") || lower.Contains("internal") || lower.Contains("server"))
        {
            return OcrErrorCodes.Unavailable;
        }

        return lower.Contains("apikey") || lower.Contains("api_key") || lower.Contains("auth") || lower.Contains("permission") || lower.Contains("access")
            ? OcrErrorCodes.NotConfigured
            : OcrErrorCodes.BadResponse;
    }

    [GeneratedRegex(@"^\s*```[A-Za-z]*\s*(.*?)\s*```\s*$", RegexOptions.Singleline)]
    private static partial Regex Fence();

    private sealed class Accumulator
    {
        private readonly StringBuilder _text = new();
        private string? _finishReason;
        private GeminiUsage _usage = NoUsage;
        private string? _error;

        public bool Complete { get; set; }

        /// <summary>One chunk (<paramref name="messageProperty"/> "delta") or the whole body ("message").</summary>
        public void Apply(string json, string messageProperty)
        {
            try
            {
                using var document = JsonDocument.Parse(json);
                var root = document.RootElement;
                if (root.ValueKind != JsonValueKind.Object)
                {
                    _error ??= "bad_chunk";
                    return;
                }

                if (ErrorOf(root) is { } error)
                {
                    _error ??= error;
                    return;
                }

                if (root.TryGetProperty("usage", out var usage) && usage.ValueKind == JsonValueKind.Object)
                {
                    _usage = new GeminiUsage(
                        Count(usage, "prompt_tokens"),
                        Count(usage, "completion_tokens"),
                        usage.TryGetProperty("completion_tokens_details", out var details) && details.ValueKind == JsonValueKind.Object ? Count(details, "reasoning_tokens") : null);
                }

                if (root.TryGetProperty("choices", out var choices) && choices.ValueKind == JsonValueKind.Array && choices.GetArrayLength() > 0)
                {
                    var choice = choices[0];
                    if (choice.TryGetProperty("finish_reason", out var reason) && reason.ValueKind == JsonValueKind.String)
                    {
                        _finishReason = reason.GetString();
                    }

                    if (choice.TryGetProperty(messageProperty, out var message) && message.ValueKind == JsonValueKind.Object
                        && message.TryGetProperty("content", out var content) && content.ValueKind == JsonValueKind.String)
                    {
                        _text.Append(content.GetString());
                    }
                }
            }
            catch (JsonException)
            {
                _error ??= "bad_chunk";
            }
        }

        public QwenCompletion ToCompletion() => new(_text.ToString(), _finishReason, _usage, Complete, _error);

        /// <summary>The code of <c>{"error":{"code":...}}</c> or of DashScope's flat <c>{"code":...,"message":...}</c>; null for a normal chunk.</summary>
        private static string? ErrorOf(JsonElement root)
        {
            if (root.TryGetProperty("error", out var error) && error.ValueKind is JsonValueKind.Object or JsonValueKind.String)
            {
                return error.ValueKind == JsonValueKind.Object ? CodeText(error, null) : CodeText(root, error.GetString());
            }

            return !root.TryGetProperty("choices", out _) && root.TryGetProperty("code", out var code) && code.ValueKind == JsonValueKind.String
                ? CodeText(root, null)
                : null;
        }

        private static string CodeText(JsonElement holder, string? fallback)
        {
            var text = holder.TryGetProperty("code", out var code) && code.ValueKind == JsonValueKind.String ? code.GetString()
                : holder.TryGetProperty("type", out var type) && type.ValueKind == JsonValueKind.String ? type.GetString()
                : fallback;
            // A code is an identifier: anything longer or odd is not logged as one.
            return string.IsNullOrWhiteSpace(text) || text.Length > 64 || text.Any(c => !(char.IsAsciiLetterOrDigit(c) || c is '.' or '_' or '-')) ? "error" : text;
        }

        private static int? Count(JsonElement holder, string name) =>
            holder.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var count) ? count : null;
    }
}
