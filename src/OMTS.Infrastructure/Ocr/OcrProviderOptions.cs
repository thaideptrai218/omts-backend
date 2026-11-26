using OMTS.Infrastructure.Ocr.Gemini;

namespace OMTS.Infrastructure.Ocr;

/// <summary>
/// "Ocr" section: the provider and its settings. <c>Gemini</c> reads meter photos, licence maps and raft forms with Google
/// Gemini (docs/ocr-gemini.md); <c>Fake</c> is set in Development and test settings only. Unset or any other value fails
/// closed (<see cref="NotConfiguredOcrService"/>) so a deployment never invents readings.
/// </summary>
public sealed class OcrProviderOptions
{
    public const string SectionName = "Ocr";
    public const string FakeProvider = "Fake";
    public const string GeminiProvider = "Gemini";

    public string Provider { get; set; } = "";
    public FakeOcrOptions Fake { get; set; } = new();
    public GeminiOptions Gemini { get; set; } = new();
    public ScanQueueOptions Scans { get; set; } = new();
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
