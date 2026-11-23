using OMTS.Infrastructure.Ocr.Paddle;

namespace OMTS.Infrastructure.Ocr;

/// <summary>
/// "Ocr" section: the provider and its settings. <c>Paddle</c> reads meter photos with the PaddleOCR sidecar
/// (tools/ocr-service, docs/deployment-ocr.md); <c>Fake</c> is set in Development and test settings only. Unset or any
/// other value fails closed (<see cref="NotConfiguredOcrService"/>) so a deployment never invents readings.
/// </summary>
public sealed class OcrProviderOptions
{
    public const string SectionName = "Ocr";
    public const string FakeProvider = "Fake";
    public const string PaddleProvider = "Paddle";

    public string Provider { get; set; } = "";
    public FakeOcrOptions Fake { get; set; } = new();
    public PaddleOcrOptions Paddle { get; set; } = new();
}

/// <summary>The PaddleOCR sidecar and the meter it reads.</summary>
public sealed class PaddleOcrOptions
{
    /// <summary>Base URL of the sidecar inside the compose network, e.g. http://ocr:8000. Required with the Paddle provider.</summary>
    public string BaseUrl { get; set; } = "";

    /// <summary>
    /// Backstop for one HTTP call; <c>Ocr:TimeoutSeconds</c> is the timeout the user waits for. Kept longer so the
    /// recognizer, not the client, decides and logs the timeout.
    /// </summary>
    public int HttpTimeoutSeconds { get; set; } = 30;

    /// <summary>Decimals the cooperative's meter shows, to put back a decimal point the OCR lost (OMTS-228: meter model to confirm).</summary>
    public int PhDecimals { get; set; } = 2;

    public int SalinityDecimals { get; set; } = 1;
    public int TemperatureDecimals { get; set; } = 1;

    /// <summary>
    /// Order of the values on the meter display when they carry no readable label, top to bottom, comma separated, e.g.
    /// "Ph,Temperature". Empty (default) means an unlabelled value is never assigned: set it once the meter model is known.
    /// </summary>
    public string Layout { get; set; } = "";

    /// <summary>The meter profile the parser uses; an unknown indicator name in <see cref="Layout"/> throws (checked at startup).</summary>
    public MeterProfile Profile() => new(PhDecimals, SalinityDecimals, TemperatureDecimals,
        Layout.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(name => Enum.TryParse<MeterIndicator>(name, ignoreCase: true, out var indicator) && Enum.IsDefined(indicator)
                ? indicator
                : throw new InvalidOperationException($"{OcrProviderOptions.SectionName}:Paddle:Layout has an unknown indicator '{name}' (use Ph, Salinity, Temperature)."))
            .ToList());
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
