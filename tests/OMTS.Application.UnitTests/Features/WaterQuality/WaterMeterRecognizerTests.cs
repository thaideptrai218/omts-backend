using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using OMTS.Application.Common.Interfaces.Ocr;
using OMTS.Application.Features.WaterQuality;
using OMTS.Application.Features.WaterQuality.Dtos;

namespace OMTS.Application.UnitTests.Features.WaterQuality;

/// <summary>WATER-01 OCR step: never blocks the user, flags uncertain fields, drops impossible readings, names what was not read.</summary>
public class WaterMeterRecognizerTests
{
    [Fact]
    public async Task Fields_below_the_confidence_threshold_are_flagged_uncertain()
    {
        var ocr = new StubOcr(_ => Task.FromResult(OcrResult.Read(new(7.8m, 0.95), new(25.5m, 0.6), new(28.5m, 0.79), null)));

        var outcome = await Recognizer(ocr).RecognizeAsync(new MemoryStream([1]), "image/jpeg", CancellationToken.None);

        Assert.Equal(OcrStatus.Success, outcome.Status);
        Assert.Equal(new OcrFieldView<decimal>(7.8m, 0.95, false), outcome.Ph);
        Assert.Equal(new OcrFieldView<decimal>(25.5m, 0.6, true), outcome.Salinity);
        Assert.Equal(new OcrFieldView<decimal>(28.5m, 0.79, true), outcome.Temperature);
        Assert.Null(outcome.MeasuredAt);
        Assert.Empty(outcome.Unread);
        Assert.Null(outcome.Warning);
    }

    [Fact]
    public async Task Provider_failure_becomes_a_failed_outcome_with_MSG_WATER_01_for_every_value()
    {
        var outcome = await Recognizer(new StubOcr(_ => Task.FromResult(OcrResult.Failed("DOWN")))).RecognizeAsync(new MemoryStream([1]), "image/png", CancellationToken.None);

        Assert.Equal(OcrStatus.Failed, outcome.Status);
        Assert.Equal("MSG-WATER-01", outcome.Warning!.Code);
        Assert.Equal("MSG04", outcome.Warning.LegacyCode);
        Assert.StartsWith("OMTS could not read pH, salinity and temperature from the photo.", outcome.Warning.Text);
        Assert.Equal(["PH", "SALINITY", "TEMPERATURE"], outcome.Unread);
    }

    [Fact]
    public async Task Provider_exception_and_timeout_never_reach_the_caller()
    {
        var throwing = await Recognizer(new StubOcr(_ => throw new HttpRequestException("boom"))).RecognizeAsync(new MemoryStream([1]), "image/png", CancellationToken.None);
        // Ignores the token on purpose: the recognizer still stops waiting after the timeout.
        var hanging = await Recognizer(new StubOcr(_ => new TaskCompletionSource<OcrResult>().Task), timeoutSeconds: 1)
            .RecognizeAsync(new MemoryStream([1]), "image/png", CancellationToken.None);

        Assert.Equal(OcrStatus.Failed, throwing.Status);
        Assert.Equal(OcrStatus.Failed, hanging.Status);
    }

    [Fact]
    public async Task Readings_outside_the_valid_ranges_or_in_the_future_are_treated_as_not_read()
    {
        var ocr = new StubOcr(_ => Task.FromResult(OcrResult.Read(new(15.2m, 0.9), new(50.5m, 0.9), new(45.5m, 0.9), new(DateTimeOffset.UtcNow.AddHours(1), 0.9))));

        var outcome = await Recognizer(ocr).RecognizeAsync(new MemoryStream([1]), "image/jpeg", CancellationToken.None);

        Assert.Equal(OcrStatus.Success, outcome.Status);
        Assert.Null(outcome.Ph);
        Assert.Null(outcome.Salinity);
        Assert.Null(outcome.Temperature);
        Assert.Null(outcome.MeasuredAt);
        Assert.Equal(["PH", "SALINITY", "TEMPERATURE"], outcome.Unread);
    }

    // MSG-WATER-01: only the values not read are named; a time not read is not an indicator (step 6 fills in the current time).
    [Theory]
    [InlineData(true, false, true, "salinity", "SALINITY")]
    [InlineData(false, true, false, "pH and temperature", "PH,TEMPERATURE")]
    [InlineData(true, true, false, "temperature", "TEMPERATURE")]
    public async Task A_partial_read_warns_naming_the_values_not_read(bool ph, bool salinity, bool temperature, string indicators, string unread)
    {
        var ocr = new StubOcr(_ => Task.FromResult(OcrResult.Read(
            ph ? new(7.8m, 0.9) : null, salinity ? new(25m, 0.9) : null, temperature ? new(28m, 0.9) : null, null)));

        var outcome = await Recognizer(ocr).RecognizeAsync(new MemoryStream([1]), "image/jpeg", CancellationToken.None);

        Assert.Equal(OcrStatus.Success, outcome.Status);
        Assert.Equal(unread.Split(','), outcome.Unread);
        Assert.Equal("MSG-WATER-01", outcome.Warning!.Code);
        Assert.Equal($"OMTS could not read {indicators} from the photo. Please look at the meter and type in the missing values.", outcome.Warning.Text);
    }

    private static WaterMeterRecognizer Recognizer(IOcrService ocr, int timeoutSeconds = 15) => new(
        ocr, Options.Create(new OcrOptions { TimeoutSeconds = timeoutSeconds, UncertainBelow = 0.8 }), TimeProvider.System, NullLogger<WaterMeterRecognizer>.Instance);

    private sealed class StubOcr(Func<CancellationToken, Task<OcrResult>> answer) : IOcrService
    {
        public Task<OcrResult> RecognizeAsync(Stream photo, string contentType, CancellationToken cancellationToken) => answer(cancellationToken);
    }
}
