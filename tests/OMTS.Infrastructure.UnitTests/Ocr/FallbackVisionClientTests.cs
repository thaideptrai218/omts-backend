using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using OMTS.Application.Common.Interfaces.Ocr;
using OMTS.Infrastructure.Ocr;

namespace OMTS.Infrastructure.UnitTests.Ocr;

/// <summary>When Gemini's read fails, the same photo is read once more by Qwen, within one time budget.</summary>
public class FallbackVisionClientTests
{
    private static readonly JsonElement Schema = JsonSerializer.Deserialize<JsonElement>("{}");

    private static async Task<(GeminiJsonResult Result, ScriptedClient Gemini, ScriptedClient Qwen, CapturingLoggerProvider Logs)> RunAsync(
        Func<int, GeminiJsonResult> gemini, Func<int, GeminiJsonResult> qwen, GeminiReader reader = GeminiReader.ZoneMap,
        ManualClock? clock = null, CancellationToken cancellation = default)
    {
        clock ??= new ManualClock();
        var primary = new ScriptedClient(gemini);
        var fallback = new ScriptedClient(qwen);
        var logs = new CapturingLoggerProvider();
        using var factory = LoggerFactory.Create(b => b.AddProvider(logs));
        var options = Options.Create(new OcrProviderOptions { Provider = OcrProviderOptions.GeminiProvider, Fallback = OcrProviderOptions.QwenProvider });
        var client = new FallbackVisionClient(primary, "Gemini", fallback, "Qwen", options, clock, factory.CreateLogger<FallbackVisionClient>());
        var result = await client.GenerateJsonAsync(reader, new byte[] { 1, 2, 3 }, "image/jpeg", "prompt", Schema, cancellation);
        return (result, primary, fallback, logs);
    }

    [Fact]
    public async Task A_good_primary_read_never_calls_the_fallback()
    {
        var run = await RunAsync(_ => ScriptedClient.Good(), _ => ScriptedClient.Good());

        Assert.True(run.Result.Ok);
        Assert.Equal(1, run.Gemini.Calls);
        Assert.Equal(0, run.Qwen.Calls);
        Assert.Contains(run.Logs.Lines, l => l.Contains("read by Gemini"));
    }

    [Theory]
    [InlineData(OcrErrorCodes.Unavailable)]
    [InlineData(OcrErrorCodes.NotConfigured)]
    [InlineData(OcrErrorCodes.Timeout)]
    [InlineData(OcrErrorCodes.BadResponse)]
    public async Task Every_failure_of_the_primary_is_followed_by_one_read_with_the_fallback(string code)
    {
        var run = await RunAsync(_ => ScriptedClient.Failed(code), _ => ScriptedClient.Good());

        Assert.True(run.Result.Ok);
        Assert.Equal(1, run.Gemini.Calls);
        Assert.Equal(1, run.Qwen.Calls);
        Assert.Contains(run.Logs.Lines, l => l.Contains("read by fallback Qwen") && l.Contains(code));
    }

    [Fact]
    public async Task When_both_fail_the_result_is_the_primary_code_and_each_is_called_once()
    {
        var run = await RunAsync(_ => ScriptedClient.Failed(OcrErrorCodes.Unavailable), _ => ScriptedClient.Failed(OcrErrorCodes.NotConfigured));

        Assert.False(run.Result.Ok);
        Assert.Equal(OcrErrorCodes.Unavailable, run.Result.ErrorCode);
        Assert.Equal(1, run.Gemini.Calls);
        Assert.Equal(1, run.Qwen.Calls);
        Assert.Contains(run.Logs.Lines, l => l.Contains("Qwen ended NOT_CONFIGURED too"));
    }

    [Fact]
    public async Task The_callers_cancellation_is_not_a_failure_to_retry()
    {
        using var cancelled = new CancellationTokenSource();
        var primary = new ScriptedClient(_ => ScriptedClient.Failed(OcrErrorCodes.Unavailable));
        var fallback = new ScriptedClient(_ => ScriptedClient.Good());
        var client = new FallbackVisionClient(
            new CancellingAfterCall(primary, cancelled), "Gemini", fallback, "Qwen",
            Options.Create(new OcrProviderOptions()), new ManualClock(), LoggerFactory.Create(_ => { }).CreateLogger<FallbackVisionClient>());

        var result = await client.GenerateJsonAsync(GeminiReader.Meter, new byte[] { 1 }, "image/jpeg", "p", Schema, cancelled.Token);

        Assert.Equal(OcrErrorCodes.Unavailable, result.ErrorCode);
        Assert.Equal(0, fallback.Calls);
    }

    [Fact]
    public async Task A_scan_whose_primary_read_used_most_of_the_budget_still_tries_the_fallback_with_what_is_left()
    {
        var clock = new ManualClock();
        // 115 s budget, 100 s spent: 15 s left, above the 10 s minimum.
        var run = await RunAsync(_ => { clock.Advance(TimeSpan.FromSeconds(100)); return ScriptedClient.Failed(OcrErrorCodes.Timeout); }, _ => ScriptedClient.Good(), clock: clock);

        Assert.True(run.Result.Ok);
        Assert.Equal(1, run.Qwen.Calls);
    }

    [Fact]
    public async Task A_scan_whose_primary_read_used_the_whole_budget_does_not_start_the_fallback()
    {
        var clock = new ManualClock();
        var run = await RunAsync(_ => { clock.Advance(TimeSpan.FromSeconds(110)); return ScriptedClient.Failed(OcrErrorCodes.Timeout); }, _ => ScriptedClient.Good(), clock: clock);

        Assert.Equal(OcrErrorCodes.Timeout, run.Result.ErrorCode);
        Assert.Equal(0, run.Qwen.Calls);
        Assert.Contains(run.Logs.Lines, l => l.Contains("Qwen is not tried"));
    }

    [Fact]
    public async Task The_meter_budget_is_the_meter_timeout_so_the_answer_stays_inside_the_request()
    {
        // The Gemini meter timeout is 25 s: 20 s spent leaves 5 s, too little for a second read.
        var clock = new ManualClock();
        var run = await RunAsync(_ => { clock.Advance(TimeSpan.FromSeconds(20)); return ScriptedClient.Failed(OcrErrorCodes.Unavailable); }, _ => ScriptedClient.Good(), GeminiReader.Meter, clock);

        Assert.Equal(0, run.Qwen.Calls);

        var quick = await RunAsync(_ => ScriptedClient.Failed(OcrErrorCodes.Unavailable), _ => ScriptedClient.Good(), GeminiReader.Meter);
        Assert.True(quick.Result.Ok);
    }

    [Theory]
    [InlineData(OcrErrorCodes.Unavailable, true)]
    [InlineData(OcrErrorCodes.NotConfigured, true)]
    [InlineData(OcrErrorCodes.Timeout, true)]
    [InlineData(OcrErrorCodes.BadResponse, true)]
    [InlineData("SOMETHING_NEW", false)]
    public void The_decision_matrix(string code, bool fallsBack) => Assert.Equal(fallsBack, FallbackVisionClient.FallsBack(code));

    /// <summary>Calls the inner client, then cancels, like a caller whose request ended during the read.</summary>
    private sealed class CancellingAfterCall(IGeminiVisionClient inner, CancellationTokenSource source) : IGeminiVisionClient
    {
        public async Task<GeminiJsonResult> GenerateJsonAsync(
            GeminiReader reader, ReadOnlyMemory<byte> image, string mimeType, string prompt, JsonElement responseSchema, CancellationToken cancellationToken)
        {
            var result = await inner.GenerateJsonAsync(reader, image, mimeType, prompt, responseSchema, cancellationToken);
            await source.CancelAsync();
            return result;
        }
    }
}
