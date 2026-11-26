using OMTS.Application.Common.Interfaces.Ocr;

namespace OMTS.Infrastructure.Ocr.Gemini;

/// <summary>"Ocr:Gemini": the Google Gemini API key, endpoint and one model/timeout per reader.</summary>
public sealed class GeminiOptions
{
    public const string DefaultBaseUrl = "https://generativelanguage.googleapis.com/v1beta/";

    /// <summary>Required with the Gemini provider. From the environment or user-secrets only; never in appsettings or a log.</summary>
    public string ApiKey { get; set; } = "";

    /// <summary>Must be https: the photo is sent to this address and nowhere else.</summary>
    public string BaseUrl { get; set; } = DefaultBaseUrl;

    /// <summary>Pause before the single retry of a 429/503, in milliseconds.</summary>
    public int RetryPauseMs { get; set; } = 1000;

    public GeminiReaders Readers { get; set; } = new();

    /// <summary>What is wrong with the settings of the Gemini provider (never the key itself), or null.</summary>
    public string? Validate()
    {
        if (string.IsNullOrWhiteSpace(ApiKey))
        {
            return "Ocr:Gemini:ApiKey is not set.";
        }

        if (!Uri.TryCreate(BaseUrl, UriKind.Absolute, out var baseUri) || baseUri.Scheme != Uri.UriSchemeHttps)
        {
            return "Ocr:Gemini:BaseUrl must be an absolute https URL: the photo is never sent over plain http.";
        }

        foreach (var reader in Enum.GetValues<GeminiReader>())
        {
            var settings = For(reader);
            if (string.IsNullOrWhiteSpace(settings.Model) || settings.Model.Any(c => !(char.IsAsciiLetterOrDigit(c) || c is '.' or '-' or '_')) || settings.Model.Contains(".."))
            {
                return $"Ocr:Gemini:Readers:{reader}:Model is not a valid model id.";
            }

            if (settings.TimeoutSeconds <= 0 || settings.ThinkingBudget < -1)
            {
                return $"Ocr:Gemini:Readers:{reader} has a timeout of zero or a thinking budget below -1.";
            }
        }

        return null;
    }

    public GeminiReaderOptions For(GeminiReader reader) => reader switch
    {
        GeminiReader.Meter => Readers.Meter,
        GeminiReader.ZoneMap => Readers.ZoneMap,
        GeminiReader.RaftForm => Readers.RaftForm,
        _ => throw new ArgumentOutOfRangeException(nameof(reader)),
    };
}

/// <summary>"Ocr:Gemini:Readers": defaults from the spike of 25 and 26/11/2026 (meter full photo: Lite 13/14 correct; licence map: Flash).</summary>
public sealed class GeminiReaders
{
    public GeminiReaderOptions Meter { get; set; } = new() { Model = "gemini-3.5-flash-lite", TimeoutSeconds = 25 };

    public GeminiReaderOptions ZoneMap { get; set; } = new() { Model = "gemini-3.5-flash", TimeoutSeconds = 90 };

    public GeminiReaderOptions RaftForm { get; set; } = new() { Model = "gemini-3.5-flash-lite", TimeoutSeconds = 45 };
}

public sealed class GeminiReaderOptions
{
    /// <summary>Model id, e.g. gemini-3.5-flash. Only letters, digits, dot, dash and underscore.</summary>
    public string Model { get; set; } = "";

    /// <summary>The whole call, the retry included; fractions allowed. After it the result is TIMEOUT.</summary>
    public double TimeoutSeconds { get; set; } = 30;

    /// <summary>Thinking tokens (<c>thinkingConfig.thinkingBudget</c>); 0 turns thinking off, unset leaves the model default.</summary>
    public int? ThinkingBudget { get; set; }
}
