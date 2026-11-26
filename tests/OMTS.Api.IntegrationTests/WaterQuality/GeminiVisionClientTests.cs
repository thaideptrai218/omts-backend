using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using OMTS.Api.IntegrationTests.Infrastructure;
using OMTS.Application.Common.Interfaces.Ocr;
using OMTS.Infrastructure.Ocr;
using OMTS.Infrastructure.Ocr.Gemini;

namespace OMTS.Api.IntegrationTests.WaterQuality;

/// <summary>The Gemini REST client against a fake HTTP handler: no test reaches the network.</summary>
public class GeminiVisionClientTests
{
    private const string Key = "AIzaSy-test-key-must-never-be-logged";
    private const string Prompt = "Read the display. SECRET-PROMPT-WORDS";
    private static readonly byte[] Image = [0xFF, 0xD8, 0xFF, 0xE0, 1, 2, 3, 4, 5, 6, 7, 8];
    private static readonly JsonElement Schema = JsonSerializer.Deserialize<JsonElement>("""{"type":"OBJECT","properties":{"a":{"type":"STRING"}}}""");

    private static string Answer(string text, string finishReason = "STOP") => JsonSerializer.Serialize(new
    {
        candidates = new[] { new { content = new { role = "model", parts = new[] { new { text } } }, finishReason } },
        usageMetadata = new { promptTokenCount = 1200, candidatesTokenCount = 40, thoughtsTokenCount = 7 },
    });

    private static HttpResponseMessage Json(HttpStatusCode status, string body) =>
        new(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    private static Task<GeminiJsonResult> CallAsync(
        FakeGeminiHandler handler, CapturingLoggerProvider? logs = null, Action<OcrProviderOptions>? configure = null, CancellationToken cancellation = default,
        GeminiReader reader = GeminiReader.Meter)
    {
        var options = new OcrProviderOptions { Provider = OcrProviderOptions.GeminiProvider };
        options.Gemini.ApiKey = Key;
        options.Gemini.RetryPauseMs = 0;
        configure?.Invoke(options);
        using var factory = LoggerFactory.Create(b => b.AddProvider(logs ?? new CapturingLoggerProvider()));
        var client = new GeminiVisionClient(
            new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan }, Options.Create(options), TimeProvider.System, factory.CreateLogger<GeminiVisionClient>());
        return client.GenerateJsonAsync(reader, Image, "image/jpeg", Prompt, Schema, cancellation);
    }

    [Fact]
    public async Task A_good_answer_is_the_parsed_json_with_the_token_usage()
    {
        var handler = new FakeGeminiHandler(_ => Json(HttpStatusCode.OK, Answer("""{"a":"7.8"}""")));

        var result = await CallAsync(handler);

        Assert.True(result.Ok);
        Assert.Equal("7.8", result.Json!.Value.GetProperty("a").GetString());
        Assert.Equal(new GeminiUsage(1200, 40, 7), result.Usage);
        Assert.Single(handler.Requests);
    }

    [Fact]
    public async Task The_request_matches_the_rest_contract_and_the_key_is_only_in_the_header()
    {
        var handler = new FakeGeminiHandler(_ => Json(HttpStatusCode.OK, Answer("{}")));

        await CallAsync(handler, configure: o => o.Gemini.Readers.Meter.ThinkingBudget = 0);

        var request = Assert.Single(handler.Requests);
        Assert.Equal("https://generativelanguage.googleapis.com/v1beta/models/gemini-3.5-flash-lite:generateContent", request.Uri);
        Assert.Equal(Key, request.ApiKeyHeader);
        Assert.DoesNotContain(Key, request.Uri);
        Assert.DoesNotContain(Key, request.Body);
        using var body = JsonDocument.Parse(request.Body);
        var parts = body.RootElement.GetProperty("contents")[0].GetProperty("parts");
        Assert.Equal("user", body.RootElement.GetProperty("contents")[0].GetProperty("role").GetString());
        Assert.Equal("image/jpeg", parts[0].GetProperty("inlineData").GetProperty("mimeType").GetString());
        Assert.Equal(Convert.ToBase64String(Image), parts[0].GetProperty("inlineData").GetProperty("data").GetString());
        Assert.Equal(Prompt, parts[1].GetProperty("text").GetString());
        var config = body.RootElement.GetProperty("generationConfig");
        Assert.Equal(0, config.GetProperty("temperature").GetInt32());
        Assert.Equal("application/json", config.GetProperty("responseMimeType").GetString());
        Assert.Equal("OBJECT", config.GetProperty("responseSchema").GetProperty("type").GetString());
        Assert.Equal(0, config.GetProperty("thinkingConfig").GetProperty("thinkingBudget").GetInt32());
    }

    [Fact]
    public async Task Without_a_thinking_budget_the_request_has_no_thinking_config_and_each_reader_uses_its_own_model()
    {
        var handler = new FakeGeminiHandler(_ => Json(HttpStatusCode.OK, Answer("{}")));

        await CallAsync(handler, reader: GeminiReader.ZoneMap);

        var request = Assert.Single(handler.Requests);
        Assert.EndsWith("/models/gemini-3.5-flash:generateContent", request.Uri);
        using var body = JsonDocument.Parse(request.Body);
        Assert.False(body.RootElement.GetProperty("generationConfig").TryGetProperty("thinkingConfig", out _));
    }

    [Fact]
    public async Task Thought_parts_are_skipped_and_text_parts_are_joined()
    {
        var body = """{"candidates":[{"content":{"parts":[{"text":"private thinking","thought":true},{"text":"{\"a\":"},{"text":"\"1\"}"}]},"finishReason":"STOP"}]}""";

        var result = await CallAsync(new FakeGeminiHandler(_ => Json(HttpStatusCode.OK, body)));

        Assert.True(result.Ok);
        Assert.Equal("1", result.Json!.Value.GetProperty("a").GetString());
    }

    [Theory]
    [InlineData("```json\n{\"a\":\"1\"}\n```")] // fenced: the schema mode never produces it, so it is refused, not repaired
    [InlineData("the display shows 7.8")]
    [InlineData("[1,2]")]
    [InlineData("")]
    public async Task Text_that_is_not_one_json_object_is_BAD_RESPONSE(string text)
    {
        var result = await CallAsync(new FakeGeminiHandler(_ => Json(HttpStatusCode.OK, Answer(text))));

        Assert.False(result.Ok);
        Assert.Equal(OcrErrorCodes.BadResponse, result.ErrorCode);
    }

    [Theory]
    [InlineData("SAFETY")]
    [InlineData("MAX_TOKENS")]
    [InlineData("RECITATION")]
    [InlineData("OTHER")]
    public async Task A_model_that_stopped_for_another_reason_than_STOP_is_BAD_RESPONSE(string finishReason)
    {
        var result = await CallAsync(new FakeGeminiHandler(_ => Json(HttpStatusCode.OK, Answer("""{"a":"1"}""", finishReason))));

        Assert.Equal(OcrErrorCodes.BadResponse, result.ErrorCode);
    }

    [Theory]
    [InlineData("""{"promptFeedback":{"blockReason":"SAFETY"}}""")]
    [InlineData("""{"candidates":[]}""")]
    [InlineData("[]")]
    [InlineData("not json at all")]
    public async Task A_blocked_prompt_or_an_answer_without_candidates_is_BAD_RESPONSE(string body)
    {
        var result = await CallAsync(new FakeGeminiHandler(_ => Json(HttpStatusCode.OK, body)));

        Assert.Equal(OcrErrorCodes.BadResponse, result.ErrorCode);
    }

    [Theory]
    [InlineData(HttpStatusCode.TooManyRequests)]
    [InlineData(HttpStatusCode.ServiceUnavailable)]
    public async Task One_retry_after_a_429_or_503_can_succeed(HttpStatusCode first)
    {
        var handler = new FakeGeminiHandler(call => call == 1 ? Json(first, """{"error":{"message":"quota"}}""") : Json(HttpStatusCode.OK, Answer("""{"a":"1"}""")));

        var result = await CallAsync(handler);

        Assert.True(result.Ok);
        Assert.Equal(2, handler.Requests.Count);
    }

    [Theory]
    [InlineData(HttpStatusCode.TooManyRequests)]
    [InlineData(HttpStatusCode.ServiceUnavailable)]
    public async Task Two_overloaded_answers_in_a_row_are_UNAVAILABLE_after_exactly_one_retry(HttpStatusCode status)
    {
        var handler = new FakeGeminiHandler(_ => Json(status, "{}"));

        var result = await CallAsync(handler);

        Assert.Equal(OcrErrorCodes.Unavailable, result.ErrorCode);
        Assert.Equal(2, handler.Requests.Count);
    }

    [Fact]
    public async Task No_retry_when_less_than_forty_percent_of_the_timeout_is_left()
    {
        // The first answer comes after 700 ms of a 1 s timeout: 30 % is left.
        var handler = new FakeGeminiHandler(_ => Json(HttpStatusCode.TooManyRequests, "{}"), delay: TimeSpan.FromMilliseconds(700));

        var result = await CallAsync(handler, configure: o => o.Gemini.Readers.Meter.TimeoutSeconds = 1);

        Assert.Equal(OcrErrorCodes.Unavailable, result.ErrorCode);
        Assert.Single(handler.Requests);
    }

    [Fact]
    public async Task A_call_that_takes_longer_than_the_reader_timeout_is_TIMEOUT()
    {
        var handler = new FakeGeminiHandler(_ => Json(HttpStatusCode.OK, Answer("{}")), delay: Timeout.InfiniteTimeSpan);

        var result = await CallAsync(handler, configure: o => o.Gemini.Readers.Meter.TimeoutSeconds = 0.2);

        Assert.Equal(OcrErrorCodes.Timeout, result.ErrorCode);
    }

    [Fact]
    public async Task The_callers_own_cancellation_is_not_an_error_result()
    {
        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();
        var handler = new FakeGeminiHandler(_ => Json(HttpStatusCode.OK, Answer("{}")), delay: Timeout.InfiniteTimeSpan);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => CallAsync(handler, cancellation: cancelled.Token));
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized)]
    [InlineData(HttpStatusCode.Forbidden)]
    [InlineData(HttpStatusCode.NotFound)]
    public async Task A_refused_request_is_NOT_CONFIGURED_and_logged_as_an_error(HttpStatusCode status)
    {
        var logs = new CapturingLoggerProvider();

        var result = await CallAsync(new FakeGeminiHandler(_ => Json(status, """{"error":{"message":"API key not valid. Please pass a valid API key."}}""")), logs);

        Assert.Equal(OcrErrorCodes.NotConfigured, result.ErrorCode);
        Assert.Contains(logs.Lines, l => l.StartsWith("Error:", StringComparison.Ordinal));
        Assert.DoesNotContain(logs.Lines, l => l.Contains("API key not valid", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(HttpStatusCode.InternalServerError, OcrErrorCodes.Unavailable)]
    [InlineData(HttpStatusCode.GatewayTimeout, OcrErrorCodes.Unavailable)]
    [InlineData(HttpStatusCode.RequestEntityTooLarge, OcrErrorCodes.BadResponse)]
    [InlineData(HttpStatusCode.BadRequest, OcrErrorCodes.BadResponse)] // Gemini answers 400 for an image it cannot process
    public async Task Other_statuses_have_a_documented_bucket(HttpStatusCode status, string expected)
    {
        var handler = new FakeGeminiHandler(_ => Json(status, "{}"));

        var result = await CallAsync(handler);

        Assert.Equal(expected, result.ErrorCode);
        Assert.Single(handler.Requests);
    }

    [Fact]
    public async Task A_network_error_is_UNAVAILABLE()
    {
        var result = await CallAsync(new FakeGeminiHandler(_ => throw new HttpRequestException("connection reset to https://generativelanguage.googleapis.com")));

        Assert.Equal(OcrErrorCodes.Unavailable, result.ErrorCode);
    }

    [Theory]
    [InlineData("", "https://generativelanguage.googleapis.com/v1beta/", "gemini-3.5-flash-lite")]
    [InlineData(Key, "http://generativelanguage.googleapis.com/v1beta/", "gemini-3.5-flash-lite")] // plain http: the photo would travel in clear
    [InlineData(Key, "https://generativelanguage.googleapis.com/v1beta/", "../models/x")]
    [InlineData(Key, "https://generativelanguage.googleapis.com/v1beta/", "gemini?key=1")]
    [InlineData(Key, "https://generativelanguage.googleapis.com/v1beta/", "")]
    public async Task A_missing_key_a_non_https_base_url_or_an_odd_model_id_sends_nothing(string key, string baseUrl, string model)
    {
        var handler = new FakeGeminiHandler(_ => Json(HttpStatusCode.OK, Answer("{}")));

        var result = await CallAsync(handler, configure: o => (o.Gemini.ApiKey, o.Gemini.BaseUrl, o.Gemini.Readers.Meter.Model) = (key, baseUrl, model));

        Assert.Equal(OcrErrorCodes.NotConfigured, result.ErrorCode);
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task The_key_the_image_the_prompt_and_the_model_text_never_reach_a_log()
    {
        var logs = new CapturingLoggerProvider();
        var scenarios = new Func<int, HttpResponseMessage>[]
        {
            _ => Json(HttpStatusCode.OK, Answer("""{"a":"MODEL-SECRET-TEXT"}""")),
            _ => Json(HttpStatusCode.OK, Answer("MODEL-SECRET-TEXT not json")),
            _ => Json(HttpStatusCode.OK, Answer("""{"a":"MODEL-SECRET-TEXT"}""", "SAFETY")),
            _ => Json(HttpStatusCode.TooManyRequests, "MODEL-SECRET-TEXT"),
            _ => Json(HttpStatusCode.Unauthorized, "MODEL-SECRET-TEXT " + Key),
            _ => throw new HttpRequestException("MODEL-SECRET-TEXT " + Key),
        };

        foreach (var scenario in scenarios)
        {
            await CallAsync(new FakeGeminiHandler(scenario), logs);
        }

        Assert.NotEmpty(logs.Lines);
        Assert.All(logs.Lines, line =>
        {
            Assert.DoesNotContain(Key, line, StringComparison.Ordinal);
            Assert.DoesNotContain("SECRET-PROMPT-WORDS", line, StringComparison.Ordinal);
            Assert.DoesNotContain("MODEL-SECRET-TEXT", line, StringComparison.Ordinal);
            Assert.DoesNotContain(Convert.ToBase64String(Image), line, StringComparison.Ordinal);
        });
    }

    [Fact]
    public async Task Successful_calls_log_status_finish_reason_tokens_and_milliseconds()
    {
        var logs = new CapturingLoggerProvider();

        await CallAsync(new FakeGeminiHandler(_ => Json(HttpStatusCode.OK, Answer("{}"))), logs);

        var line = Assert.Single(logs.Lines);
        Assert.Contains("HTTP 200", line);
        Assert.Contains("finishReason STOP", line);
        Assert.Contains("prompt 1200 output 40 thoughts 7", line);
        Assert.Contains(" ms", line);
    }
}
