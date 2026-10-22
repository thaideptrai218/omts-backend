namespace OMTS.Infrastructure.Ocr;

/// <summary>"Ocr" section: the provider (only <c>Fake</c> until the real OCR provider ticket) and the fake's answers.</summary>
public sealed class OcrProviderOptions
{
    public const string SectionName = "Ocr";
    public const string FakeProvider = "Fake";

    public string Provider { get; set; } = FakeProvider;
    public FakeOcrOptions Fake { get; set; } = new();
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
    public DateTimeOffset? MeasuredAt { get; set; }

    /// <summary>Confidence of every field read.</summary>
    public double Confidence { get; set; } = 0.95;
}
