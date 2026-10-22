using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using OMTS.Application.Common;
using OMTS.Application.Common.Interfaces.Ocr;

namespace OMTS.Infrastructure.Ocr;

/// <summary>
/// OCR Service stand-in for development and tests (no real OCR call outside Production until a provider is
/// chosen). Answers with <c>Ocr:Fake</c>, unless the photo bytes contain a marker text such as
/// <c>ph7.8_sal25.5</c> (the file-name convention of the test photos), <c>conf0.6</c> (confidence),
/// <c>at2026-10-20T08:00</c> (local reading time), <c>ocr-fail</c> or <c>ocr-timeout</c>.
/// </summary>
public sealed partial class FakeOcrService(IOptions<OcrProviderOptions> options, ILogger<FakeOcrService> logger) : IOcrService
{
    // Markers are looked for at the start of the photo, where test images carry them.
    private const int MarkerWindow = 64 * 1024;

    public async Task<OcrResult> RecognizeAsync(Stream photo, string contentType, CancellationToken cancellationToken)
    {
        var fake = options.Value.Fake;
        var text = await ReadMarkerWindowAsync(photo, cancellationToken);
        var mode = text.Contains("ocr-timeout", StringComparison.OrdinalIgnoreCase) ? FakeOcrOptions.TimeoutMode
            : text.Contains("ocr-fail", StringComparison.OrdinalIgnoreCase) ? FakeOcrOptions.FailMode
            : fake.Mode;

        if (string.Equals(mode, FakeOcrOptions.TimeoutMode, StringComparison.OrdinalIgnoreCase))
        {
            logger.LogInformation("Fake OCR: simulating a timeout");
            await Task.Delay(Timeout.Infinite, cancellationToken);
        }

        if (string.Equals(mode, FakeOcrOptions.FailMode, StringComparison.OrdinalIgnoreCase))
        {
            logger.LogInformation("Fake OCR: simulating a failure");
            return OcrResult.Failed("FAKE_FAILURE");
        }

        var confidence = Number(ConfidenceMarker().Match(text)) is { } c ? (double)c : fake.Confidence;
        var ph = Number(PhMarker().Match(text)) ?? fake.Ph;
        var salinity = Number(SalinityMarker().Match(text)) ?? fake.Salinity;
        var measuredAt = Time(TimeMarker().Match(text)) ?? fake.MeasuredAt;
        logger.LogInformation("Fake OCR read pH {Ph}, salinity {Salinity} ({ContentType})", ph, salinity, contentType);
        return OcrResult.Read(
            ph is { } p ? new OcrReading<decimal>(p, confidence) : null,
            salinity is { } s ? new OcrReading<decimal>(s, confidence) : null,
            measuredAt is { } at ? new OcrReading<DateTimeOffset>(at, confidence) : null);
    }

    private static async Task<string> ReadMarkerWindowAsync(Stream photo, CancellationToken cancellationToken)
    {
        var buffer = new byte[MarkerWindow];
        var read = await photo.ReadAtLeastAsync(buffer, buffer.Length, throwOnEndOfStream: false, cancellationToken);
        return Encoding.Latin1.GetString(buffer, 0, read);
    }

    private static decimal? Number(Match match) =>
        match.Success && decimal.TryParse(match.Groups["v"].Value, NumberStyles.Number, CultureInfo.InvariantCulture, out var value) ? value : null;

    private static DateTimeOffset? Time(Match match) =>
        match.Success && DateTime.TryParseExact(match.Groups["v"].Value, "yyyy-MM-ddTHH:mm", CultureInfo.InvariantCulture, DateTimeStyles.None, out var local)
            ? new DateTimeOffset(local, BusinessClock.UtcOffset)
            : null;

    [GeneratedRegex(@"ph(?<v>\d{1,3}(?:\.\d{1,2})?)", RegexOptions.IgnoreCase)]
    private static partial Regex PhMarker();

    [GeneratedRegex(@"sal(?<v>\d{1,3}(?:\.\d{1,2})?)", RegexOptions.IgnoreCase)]
    private static partial Regex SalinityMarker();

    [GeneratedRegex(@"conf(?<v>[01](?:\.\d{1,3})?)", RegexOptions.IgnoreCase)]
    private static partial Regex ConfidenceMarker();

    [GeneratedRegex(@"at(?<v>\d{4}-\d{2}-\d{2}T\d{2}:\d{2})", RegexOptions.IgnoreCase)]
    private static partial Regex TimeMarker();
}
