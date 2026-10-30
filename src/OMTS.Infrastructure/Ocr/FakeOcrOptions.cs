
namespace OMTS.Infrastructure.Ocr;

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
