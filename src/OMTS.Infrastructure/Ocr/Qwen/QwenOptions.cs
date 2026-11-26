using OMTS.Application.Common.Interfaces.Ocr;

namespace OMTS.Infrastructure.Ocr.Qwen;

/// <summary>
/// "Ocr:Qwen": Alibaba Cloud Model Studio (DashScope, OpenAI-compatible API) as the fallback behind Gemini, or as the primary
/// provider (<c>Ocr:Provider=Qwen</c>). See docs/ocr-qwen.md.
/// </summary>
public sealed class QwenOptions
{
    public const string DefaultBaseUrl = "https://dashscope-intl.aliyuncs.com/compatible-mode/v1/";
    public const string DefaultModel = "qwen3.8-omni-flash";

    /// <summary>Required when Qwen is the provider or the fallback. From the environment or user-secrets only; never in appsettings or a log.</summary>
    public string ApiKey { get; set; } = "";

    /// <summary>Must be https: the photo is sent to this address and nowhere else. The request goes to <c>{BaseUrl}chat/completions</c>.</summary>
    public string BaseUrl { get; set; } = DefaultBaseUrl;

    /// <summary>
    /// Sends <c>"enable_thinking": false</c> (a DashScope extension) so the model answers without its reasoning pass, which costs
    /// tokens and seconds. Set it to false to leave the field out if the endpoint or a model rejects it.
    /// </summary>
    public bool DisableThinking { get; set; } = true;

    /// <summary>Sends <c>response_format: {"type":"json_object"}</c>. Off by default: the omni models may refuse it; the prompt already asks for one JSON object.</summary>
    public bool JsonMode { get; set; }

    public QwenReaders Readers { get; set; } = new();

    /// <summary>What is wrong with the settings of Qwen (never the key itself), or null.</summary>
    public string? Validate()
    {
        if (string.IsNullOrWhiteSpace(ApiKey))
        {
            return "Ocr:Qwen:ApiKey is not set.";
        }

        if (!Uri.TryCreate(BaseUrl, UriKind.Absolute, out var baseUri) || baseUri.Scheme != Uri.UriSchemeHttps)
        {
            return "Ocr:Qwen:BaseUrl must be an absolute https URL: the photo is never sent over plain http.";
        }

        foreach (var reader in Enum.GetValues<GeminiReader>())
        {
            var settings = For(reader);
            if (!IsValidModelId(settings.Model))
            {
                return $"Ocr:Qwen:Readers:{reader}:Model is not a valid model id.";
            }

            if (settings.TimeoutSeconds <= 0)
            {
                return $"Ocr:Qwen:Readers:{reader}:TimeoutSeconds must be above zero.";
            }
        }

        return null;
    }

    public QwenReaderOptions For(GeminiReader reader) => reader switch
    {
        GeminiReader.Meter => Readers.Meter,
        GeminiReader.ZoneMap => Readers.ZoneMap,
        GeminiReader.RaftForm => Readers.RaftForm,
        _ => throw new ArgumentOutOfRangeException(nameof(reader)),
    };

    internal static bool IsValidModelId(string? model) =>
        !string.IsNullOrWhiteSpace(model) && model.All(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '-' or '_') && !model.Contains("..");
}

/// <summary>"Ocr:Qwen:Readers". The licence map took about 45 s on the sample sheet, so its timeout is 90 s.</summary>
public sealed class QwenReaders
{
    public QwenReaderOptions Meter { get; set; } = new() { Model = QwenOptions.DefaultModel, TimeoutSeconds = 25 };

    public QwenReaderOptions ZoneMap { get; set; } = new() { Model = QwenOptions.DefaultModel, TimeoutSeconds = 90 };

    public QwenReaderOptions RaftForm { get; set; } = new() { Model = QwenOptions.DefaultModel, TimeoutSeconds = 45 };
}

public sealed class QwenReaderOptions
{
    /// <summary>Model id, e.g. qwen3.8-omni-flash. Only letters, digits, dot, dash and underscore.</summary>
    public string Model { get; set; } = QwenOptions.DefaultModel;

    /// <summary>The whole call, the streamed answer included; fractions allowed. After it the result is TIMEOUT.</summary>
    public double TimeoutSeconds { get; set; } = 30;

    /// <summary>
    /// Server-sent events (<c>"stream": true</c>). The omni models require it; a model that allows a plain answer can set false.
    /// </summary>
    public bool Stream { get; set; } = true;
}
