using OMTS.Application.Common.Interfaces.Ocr;
using OMTS.Infrastructure.Ocr.Gemini;
using OMTS.Infrastructure.Ocr.Qwen;

namespace OMTS.Infrastructure.Ocr;

/// <summary>
/// "Ocr" section: the provider and its settings. <c>Gemini</c> reads meter photos, licence maps and raft forms with Google
/// Gemini (docs/ocr-gemini.md); <c>Qwen</c> reads them with Alibaba Cloud Qwen (docs/ocr-qwen.md), as the provider or, with
/// <c>Fallback=Qwen</c>, as the second try after a failed Gemini read; <c>Fake</c> is set in Development and test settings only. Unset or any other value fails
/// closed (<see cref="NotConfiguredOcrService"/>) so a deployment never invents readings.
/// </summary>
public sealed class OcrProviderOptions
{
    public const string SectionName = "Ocr";
    public const string FakeProvider = "Fake";
    public const string GeminiProvider = "Gemini";
    public const string QwenProvider = "Qwen";

    public string Provider { get; set; } = "";

    /// <summary>Empty (no fallback) or <c>Qwen</c>: the provider that reads a photo once more when the <see cref="Provider"/> (Gemini) read failed.</summary>
    public string Fallback { get; set; } = "";

    /// <summary>
    /// Time both providers share for one licence map or raft form scan: the client polls for 120 s, so 115 s by default. The fallback gets
    /// what the primary left. The meter (answered within the request) has no setting: its budget is the primary's meter timeout.
    /// </summary>
    public int ReadBudgetSeconds { get; set; } = 115;

    public FakeOcrOptions Fake { get; set; } = new();
    public GeminiOptions Gemini { get; set; } = new();
    public QwenOptions Qwen { get; set; } = new();
    public ScanQueueOptions Scans { get; set; } = new();

    /// <summary>True when a failed Gemini read is retried with Qwen.</summary>
    public bool UsesQwenFallback => Provider == GeminiProvider && Fallback == QwenProvider;

    /// <summary>True when the provider reads photos with a model (Gemini or Qwen) rather than the fake or nothing.</summary>
    public bool ReadsWithModel => Provider is GeminiProvider or QwenProvider;

    /// <summary>The provider's meter timeout: the recognizer waits this plus 2 s.</summary>
    public double MeterTimeoutSeconds => Provider == QwenProvider ? Qwen.Readers.Meter.TimeoutSeconds : Gemini.Readers.Meter.TimeoutSeconds;

    /// <summary>How long one read may take with the fallback included: the meter's own timeout, or <see cref="ReadBudgetSeconds"/> for a scan.</summary>
    public TimeSpan ReadBudget(GeminiReader reader) =>
        TimeSpan.FromSeconds(reader == GeminiReader.Meter ? MeterTimeoutSeconds : Math.Max(1, ReadBudgetSeconds));

    /// <summary>
    /// What is wrong with the provider settings (never a key), or null. Only a model provider and the fallback are checked: an unset or
    /// unknown <c>Provider</c> reads nothing and is not an error (docs/ocr-gemini.md), but a wrong <c>Fallback</c> is, so a typo cannot turn it off silently.
    /// </summary>
    public string? Validate()
    {
        if (Fallback is not ("" or QwenProvider))
        {
            return "Ocr:Fallback must be empty or Qwen.";
        }

        if (Fallback == QwenProvider && Provider == QwenProvider)
        {
            return "Ocr:Fallback=Qwen is the same as Ocr:Provider=Qwen: leave the fallback empty.";
        }

        if (Provider == GeminiProvider && Gemini.Validate() is { } gemini)
        {
            return gemini;
        }

        return (Provider == QwenProvider || UsesQwenFallback) && Qwen.Validate() is { } qwen ? qwen : null;
    }
}

/// <summary>"Ocr:Scans": the in-process queue of the asynchronous photo scans.</summary>
public sealed class ScanQueueOptions
{
    /// <summary>Scans waiting for a worker; one more is refused with OCR_BUSY (503). Small on purpose: the client gives up after 120 s.</summary>
    public int QueueCapacity { get; set; } = 4;

    /// <summary>Scans read at the same time (each holds one Gemini call).</summary>
    public int MaxConcurrent { get; set; } = 2;

    /// <summary>How long a scan, and its result, stays readable.</summary>
    public int ResultMinutes { get; set; } = 15;

    /// <summary>A worker gives up on a job after this, whatever the reader does.</summary>
    public int JobTimeoutSeconds { get; set; } = 180;

    /// <summary>A scan that waited longer than this in the queue ends Failed/TIMEOUT without calling Gemini: the client has given up (it polls 120 s).</summary>
    public int MaxQueueWaitSeconds { get; set; } = 120;
}

/// <summary>What <see cref="FakeOcrService"/> answers when the photo carries no marker.</summary>
public sealed class FakeOcrOptions
{
    public const string SuccessMode = "Success";
    public const string FailMode = "Fail";
    public const string TimeoutMode = "Timeout";

    /// <summary><c>Success</c>, <c>Fail</c> (error result) or <c>Timeout</c> (never answers).</summary>
    public string Mode { get; set; } = SuccessMode;

    public decimal? Ph { get; set; } = 7.8m;
    public decimal? Salinity { get; set; } = 25.5m;
    public decimal? Temperature { get; set; } = 28.5m;
    public DateTimeOffset? MeasuredAt { get; set; }

    /// <summary>Confidence of every field read.</summary>
    public double Confidence { get; set; } = 0.95;
}
