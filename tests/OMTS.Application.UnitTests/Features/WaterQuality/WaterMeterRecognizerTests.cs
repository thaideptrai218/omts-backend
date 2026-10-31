using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using OMTS.Application.Common.Interfaces.Ocr;
using OMTS.Application.Features.WaterQuality;
using OMTS.Application.Features.WaterQuality.Dtos;
using OMTS.Domain.Entities.Cultivation;
using OMTS.Domain.Enums;

namespace OMTS.Application.UnitTests.Features.WaterQuality;

/// <summary>ENV-01 OCR step: never blocks the user, flags uncertain fields, drops impossible readings.</summary>
public class WaterMeterRecognizerTests
{
    [Fact]
    public async Task Fields_below_the_confidence_threshold_are_flagged_uncertain()
    {
        var ocr = new StubOcr(_ => Task.FromResult(OcrResult.Read(new(7.8m, 0.95), new(25.5m, 0.6), null)));

        var outcome = await Recognizer(ocr).RecognizeAsync(new MemoryStream([1]), "image/jpeg", CancellationToken.None);

        Assert.Equal(OcrStatus.Success, outcome.Status);
        Assert.Equal(new OcrFieldView<decimal>(7.8m, 0.95, false), outcome.Ph);
        Assert.Equal(new OcrFieldView<decimal>(25.5m, 0.6, true), outcome.Salinity);
        Assert.Null(outcome.MeasuredAt);
        Assert.Null(outcome.Warning);
    }

    [Fact]
    public async Task Provider_failure_becomes_a_failed_outcome_with_MSG_WATER_01()
    {
        var outcome = await Recognizer(new StubOcr(_ => Task.FromResult(OcrResult.Failed("DOWN")))).RecognizeAsync(new MemoryStream([1]), "image/png", CancellationToken.None);

        Assert.Equal(OcrStatus.Failed, outcome.Status);
        Assert.Equal("MSG-WATER-01", outcome.Warning!.Code);
        Assert.Equal("MSG04", outcome.Warning.LegacyCode);
        Assert.StartsWith("OMTS could not read pH and salinity from the photo.", outcome.Warning.Text);
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
        var ocr = new StubOcr(_ => Task.FromResult(OcrResult.Read(new(15.2m, 0.9), new(250m, 0.9), new(DateTimeOffset.UtcNow.AddHours(1), 0.9))));

        var outcome = await Recognizer(ocr).RecognizeAsync(new MemoryStream([1]), "image/jpeg", CancellationToken.None);

        Assert.Equal(OcrStatus.Success, outcome.Status);
        Assert.Null(outcome.Ph);
        Assert.Null(outcome.Salinity);
        Assert.Null(outcome.MeasuredAt);
    }

    [Fact]
    public void Apply_fills_the_draft_but_keeps_values_entered_by_hand()
    {
        var readAt = new DateTimeOffset(2026, 10, 20, 1, 0, 0, TimeSpan.Zero);
        var outcome = new OcrOutcome(OcrStatus.Success, new(7.8m, 0.9, false), new(25.5m, 0.9, false), new(readAt, 0.9, false), null);
        var fresh = new WaterMeasurement { MeasuredAt = readAt.AddDays(2), InputSource = MeasurementInputSource.Manual };
        var typed = new WaterMeasurement { MeasuredAt = readAt.AddDays(2), Ph = 7.1m, InputSource = MeasurementInputSource.Manual };

        WaterMeterRecognizer.Apply(fresh, outcome);
        WaterMeterRecognizer.Apply(typed, outcome);

        Assert.Equal((7.8m, 25.5m, readAt, MeasurementInputSource.Ocr), (fresh.Ph!.Value, fresh.Salinity!.Value, fresh.MeasuredAt, fresh.InputSource));
        Assert.Equal((7.8m, 25.5m, readAt), (fresh.OcrPh!.Value, fresh.OcrSalinity!.Value, fresh.OcrMeasuredAt!.Value));
        Assert.Equal((7.1m, 25.5m, MeasurementInputSource.Manual), (typed.Ph!.Value, typed.Salinity!.Value, typed.InputSource));
        Assert.Equal(readAt.AddDays(2), typed.MeasuredAt);
        Assert.Equal(7.8m, typed.OcrPh);
    }

    private static WaterMeterRecognizer Recognizer(IOcrService ocr, int timeoutSeconds = 15) => new(
        ocr, Options.Create(new OcrOptions { TimeoutSeconds = timeoutSeconds, UncertainBelow = 0.8 }), TimeProvider.System, NullLogger<WaterMeterRecognizer>.Instance);

    private sealed class StubOcr(Func<CancellationToken, Task<OcrResult>> answer) : IOcrService
    {
        public Task<OcrResult> RecognizeAsync(Stream photo, string contentType, CancellationToken cancellationToken) => answer(cancellationToken);
    }
}
