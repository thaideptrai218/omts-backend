using System.Net;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using OMTS.Application.Common.Interfaces.Ocr;
using OMTS.Infrastructure.Ocr;
using OMTS.Infrastructure.Ocr.Qwen;

namespace OMTS.Infrastructure.UnitTests.Ocr;

/// <summary>The Qwen client against a fake HTTP handler: no test reaches the network.</summary>
public class QwenVisionClientTests
{
    private const string Key = "sk-test-key-must-never-be-logged";
    private const string Prompt = "Read the display. SECRET-PROMPT-WORDS";
    private static readonly byte[] Image = [0xFF, 0xD8, 0xFF, 0xE0, 1, 2, 3, 4, 5, 6, 7, 8];
    private static readonly JsonElement Schema = JsonSerializer.Deserialize<JsonElement>(
        """{"type":"OBJECT","properties":{"a":{"type":"STRING","nullable":true}},"required":["a"],"propertyOrdering":["a"]}""");

    private static Task<GeminiJsonResult> CallAsync(
        HttpMessageHandler handler, CapturingLoggerProvider? logs = null, Action<OcrProviderOptions>? configure = null, CancellationToken cancellation = default,
        GeminiReader reader = GeminiReader.Meter)
    {
        var options = new OcrProviderOptions { Provider = OcrProviderOptions.QwenProvider };
        options.Qwen.ApiKey = Key;
        configure?.Invoke(options);
        using var factory = LoggerFactory.Create(b => b.AddProvider(logs ?? new CapturingLoggerProvider()));
        var client = new QwenVisionClient(
            new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan }, Options.Create(options), TimeProvider.System, factory.CreateLogger<QwenVisionClient>());
        return client.GenerateJsonAsync(reader, Image, "image/jpeg", Prompt, Schema, cancellation);
    }

    [Fact]
    public async Task A_streamed_answer_in_several_chunks_is_the_parsed_json_with_the_token_usage()
    {
        var handler = new FakeHandler(_ => Answers.Sse(
            Answers.Delta("", reasoning: "thinking about the display"),
            Answers.Delta("""{"a":"""), Answers.Delta("\"7.8\"}", "stop"), Answers.Usage(1200, 40, 7), "[DONE]"));

        var result = await CallAsync(handler);

        Assert.True(result.Ok);
        Assert.Equal("7.8", result.Json!.Value.GetProperty("a").GetString());
        Assert.Equal(new GeminiUsage(1200, 40, 7), result.Usage);
        Assert.Single(handler.Requests);
    }

    [Fact]
    public async Task Reasoning_text_is_never_part_of_the_answer()
    {
        var handler = new FakeHandler(_ => Answers.Sse(
            Answers.Delta("", reasoning: "{\"a\":\"from the reasoning\"}"), Answers.Delta("""{"a":"answer"}""", "stop"), "[DONE]"));

        var result = await CallAsync(handler);

        Assert.Equal("answer", result.Json!.Value.GetProperty("a").GetString());
    }

    [Theory]
    [InlineData("```json\n{\"a\":\"1\"}\n```")]
    [InlineData("```\n{\"a\":\"1\"}\n```")]
    [InlineData("  {\"a\":\"1\"}  ")]
    public async Task A_markdown_fence_around_the_json_is_removed(string text)
    {
        var handler = new FakeHandler(_ => Answers.Sse(Answers.Delta(text, "stop"), "[DONE]"));

        var result = await CallAsync(handler);

        Assert.True(result.Ok);
        Assert.Equal("1", result.Json!.Value.GetProperty("a").GetString());
    }

    [Fact]
    public async Task Comment_lines_blank_lines_and_event_fields_of_the_stream_are_ignored()
    {
        var body = ": keep-alive\n\nevent: message\ndata: " + Answers.Delta("""{"a":"1"}""", "stop") + "\n\ndata:\n\ndata: [DONE]\n\n";
        var handler = new FakeHandler(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body) });

        Assert.True((await CallAsync(handler)).Ok);
    }

    [Fact]
    public async Task A_stream_without_a_finish_reason_is_accepted_only_when_it_ends_with_DONE()
    {
        var complete = await CallAsync(new FakeHandler(_ => Answers.Sse(Answers.Delta("""{"a":"1"}"""), "[DONE]")));
        var cutOff = await CallAsync(new FakeHandler(_ => Answers.Sse(Answers.Delta("""{"a":"1"}"""))));

        Assert.True(complete.Ok);
        Assert.Equal(OcrErrorCodes.BadResponse, cutOff.ErrorCode);
    }

    [Theory]
    [InlineData("length")]
    [InlineData("content_filter")]
    public async Task A_model_that_stopped_early_is_BAD_RESPONSE(string finishReason)
    {
        var handler = new FakeHandler(_ => Answers.Sse(Answers.Delta("""{"a":"1"}""", finishReason), "[DONE]"));

        var result = await CallAsync(handler);

        Assert.False(result.Ok);
        Assert.Equal(OcrErrorCodes.BadResponse, result.ErrorCode);
    }

    [Theory]
    [InlineData("not json at all")]
    [InlineData("[1,2]")]
    [InlineData("")]
    public async Task An_answer_that_is_not_one_json_object_is_BAD_RESPONSE(string text)
    {
        var handler = new FakeHandler(_ => Answers.Sse(Answers.Delta(text, "stop"), "[DONE]"));

        Assert.Equal(OcrErrorCodes.BadResponse, (await CallAsync(handler)).ErrorCode);
    }

    [Theory]
    [InlineData("""{"error":{"code":"Throttling.RateQuota","message":"secret words"}}""", OcrErrorCodes.Unavailable)]
    [InlineData("""{"code":"limit_requests","message":"secret words"}""", OcrErrorCodes.Unavailable)]
    [InlineData("""{"error":{"code":"InvalidApiKey","message":"secret words"}}""", OcrErrorCodes.NotConfigured)]
    [InlineData("""{"error":{"code":"InvalidParameter","message":"secret words"}}""", OcrErrorCodes.BadResponse)]
    public async Task An_error_chunk_in_the_stream_is_a_failure_whatever_came_before(string errorChunk, string expected)
    {
        var logs = new CapturingLoggerProvider();
        var handler = new FakeHandler(_ => Answers.Sse(Answers.Delta("""{"a":"1"}"""), errorChunk));

        var result = await CallAsync(handler, logs);

        Assert.Equal(expected, result.ErrorCode);
        Assert.DoesNotContain(logs.Lines, line => line.Contains("secret words"));
    }

    [Fact]
    public async Task A_garbled_chunk_is_BAD_RESPONSE()
    {
        var handler = new FakeHandler(_ => Answers.Sse("{not json", "[DONE]"));

        Assert.Equal(OcrErrorCodes.BadResponse, (await CallAsync(handler)).ErrorCode);
    }

    [Fact]
    public async Task A_plain_answer_is_read_when_the_model_is_set_not_to_stream()
    {
        var handler = new FakeHandler(_ => Answers.Json(HttpStatusCode.OK, Answers.Body("""{"a":"2"}""")));

        var result = await CallAsync(handler, configure: o => o.Qwen.Readers.Meter.Stream = false);

        Assert.True(result.Ok);
        Assert.Equal("2", result.Json!.Value.GetProperty("a").GetString());
        Assert.Equal(new GeminiUsage(900, 30, 0), result.Usage);
        using var body = JsonDocument.Parse(handler.Requests.Single().Body);
        Assert.False(body.RootElement.GetProperty("stream").GetBoolean());
        Assert.False(body.RootElement.TryGetProperty("stream_options", out _));
    }

    [Fact]
    public async Task The_request_matches_the_chat_completions_contract_and_the_key_is_only_in_the_header()
    {
        var handler = new FakeHandler(_ => Answers.GoodStream());

        await CallAsync(handler);

        var request = Assert.Single(handler.Requests);
        Assert.Equal("https://dashscope-intl.aliyuncs.com/compatible-mode/v1/chat/completions", request.Uri);
        Assert.Equal("Bearer " + Key, request.Authorization);
        Assert.DoesNotContain(Key, request.Uri);
        Assert.DoesNotContain(Key, request.Body);
        using var body = JsonDocument.Parse(request.Body);
        var root = body.RootElement;
        Assert.Equal("qwen3.8-omni-flash", root.GetProperty("model").GetString());
        Assert.True(root.GetProperty("stream").GetBoolean());
        Assert.True(root.GetProperty("stream_options").GetProperty("include_usage").GetBoolean());
        Assert.False(root.GetProperty("enable_thinking").GetBoolean());
        Assert.False(root.TryGetProperty("response_format", out _));
        var message = root.GetProperty("messages")[0];
        Assert.Equal("user", message.GetProperty("role").GetString());
        var content = message.GetProperty("content");
        Assert.Equal("image_url", content[0].GetProperty("type").GetString());
        Assert.Equal("data:image/jpeg;base64," + Convert.ToBase64String(Image), content[0].GetProperty("image_url").GetProperty("url").GetString());
        Assert.Equal("text", content[1].GetProperty("type").GetString());
        var text = content[1].GetProperty("text").GetString()!;
        Assert.StartsWith(Prompt, text);
        // The schema is written into the prompt: the key and its type.
        Assert.Contains("\"a\": string or null", text);
    }

    [Fact]
    public async Task The_thinking_switch_and_json_mode_follow_the_settings_and_the_model_the_reader_setting()
    {
        var handler = new FakeHandler(_ => Answers.GoodStream());

        await CallAsync(handler, configure: o =>
        {
            o.Qwen.DisableThinking = false;
            o.Qwen.JsonMode = true;
            o.Qwen.Readers.ZoneMap.Model = "qwen-custom_1.2";
        }, reader: GeminiReader.ZoneMap);

        using var body = JsonDocument.Parse(handler.Requests.Single().Body);
        Assert.False(body.RootElement.TryGetProperty("enable_thinking", out _));
        Assert.Equal("json_object", body.RootElement.GetProperty("response_format").GetProperty("type").GetString());
        Assert.Equal("qwen-custom_1.2", body.RootElement.GetProperty("model").GetString());
    }

    [Theory]
    [InlineData(401, OcrErrorCodes.NotConfigured)]
    [InlineData(403, OcrErrorCodes.NotConfigured)]
    [InlineData(429, OcrErrorCodes.Unavailable)]
    [InlineData(500, OcrErrorCodes.Unavailable)]
    [InlineData(503, OcrErrorCodes.Unavailable)]
    [InlineData(400, OcrErrorCodes.BadResponse)]
    [InlineData(404, OcrErrorCodes.BadResponse)]
    [InlineData(413, OcrErrorCodes.BadResponse)]
    public async Task An_http_error_maps_to_the_failure_code_and_is_not_retried(int status, string expected)
    {
        var logs = new CapturingLoggerProvider();
        var handler = new FakeHandler(_ => Answers.Json((HttpStatusCode)status, """{"error":{"message":"secret words from the provider"}}"""));

        var result = await CallAsync(handler, logs);

        Assert.False(result.Ok);
        Assert.Equal(expected, result.ErrorCode);
        Assert.Single(handler.Requests);
        Assert.DoesNotContain(logs.Lines, line => line.Contains("secret words"));
    }

    [Fact]
    public async Task A_refused_key_is_logged_as_an_error_without_the_key()
    {
        var logs = new CapturingLoggerProvider();

        await CallAsync(new FakeHandler(_ => Answers.Json(HttpStatusCode.Unauthorized, "{}")), logs);

        Assert.Contains(logs.Lines, line => line.StartsWith("Error:") && line.Contains("401"));
        Assert.DoesNotContain(logs.Lines, line => line.Contains(Key));
    }

    [Fact]
    public async Task No_answer_within_the_reader_timeout_is_TIMEOUT()
    {
        var handler = new FakeHandler(_ => Answers.GoodStream(), delay: TimeSpan.FromSeconds(30));

        var result = await CallAsync(handler, configure: o => o.Qwen.Readers.Meter.TimeoutSeconds = 0.1);

        Assert.Equal(OcrErrorCodes.Timeout, result.ErrorCode);
    }

    [Fact]
    public async Task A_stream_that_stalls_after_the_headers_is_TIMEOUT()
    {
        var handler = new FakeHandler(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(new StallingStream("data: {\"choices\":[]}\n")) });

        var result = await CallAsync(handler, configure: o => o.Qwen.Readers.Meter.TimeoutSeconds = 0.2);

        Assert.Equal(OcrErrorCodes.Timeout, result.ErrorCode);
    }

    [Fact]
    public async Task The_callers_own_cancellation_is_not_a_failed_result()
    {
        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => CallAsync(new FakeHandler(_ => Answers.GoodStream(), delay: TimeSpan.FromSeconds(30)), cancellation: cancelled.Token));
    }

    [Fact]
    public async Task A_network_error_is_UNAVAILABLE_and_its_message_is_not_logged()
    {
        var logs = new CapturingLoggerProvider();
        var handler = new ThrowingHandler(new HttpRequestException("connect failed to dashscope-intl.aliyuncs.com"));

        var result = await CallAsync(handler, logs);

        Assert.Equal(OcrErrorCodes.Unavailable, result.ErrorCode);
        Assert.DoesNotContain(logs.Lines, line => line.Contains("aliyuncs"));
    }

    [Theory]
    [InlineData("")]
    [InlineData("  ")]
    public async Task Without_a_key_nothing_is_sent_and_the_result_is_NOT_CONFIGURED(string key)
    {
        var handler = new FakeHandler(_ => Answers.GoodStream());

        var result = await CallAsync(handler, configure: o => o.Qwen.ApiKey = key);

        Assert.Equal(OcrErrorCodes.NotConfigured, result.ErrorCode);
        Assert.Empty(handler.Requests);
    }

    [Theory]
    [InlineData("http://dashscope-intl.aliyuncs.com/compatible-mode/v1/")]
    [InlineData("not a url")]
    public async Task A_plain_http_or_invalid_base_url_is_never_called(string baseUrl)
    {
        var handler = new FakeHandler(_ => Answers.GoodStream());

        var result = await CallAsync(handler, configure: o => o.Qwen.BaseUrl = baseUrl);

        Assert.Equal(OcrErrorCodes.NotConfigured, result.ErrorCode);
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task The_log_holds_status_finish_reason_tokens_and_time_but_never_the_key_the_image_the_prompt_or_the_text()
    {
        var logs = new CapturingLoggerProvider();
        var handler = new FakeHandler(_ => Answers.GoodStream("""{"a":"MODEL-TEXT-SECRET"}"""));

        await CallAsync(handler, logs);

        var line = Assert.Single(logs.Lines, l => l.Contains("HTTP 200"));
        Assert.Contains("finishReason stop", line);
        Assert.Contains("prompt 1200 output 40 reasoning 7", line);
        Assert.All(logs.Lines, l =>
        {
            Assert.DoesNotContain(Key, l);
            Assert.DoesNotContain("SECRET-PROMPT-WORDS", l);
            Assert.DoesNotContain("MODEL-TEXT-SECRET", l);
            Assert.DoesNotContain(Convert.ToBase64String(Image), l);
        });
    }

    private sealed class ThrowingHandler(Exception exception) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => throw exception;
    }

    /// <summary>Gives its first bytes, then waits until the read is cancelled, like a server that stops sending.</summary>
    private sealed class StallingStream(string first) : Stream
    {
        private readonly MemoryStream _first = new(System.Text.Encoding.UTF8.GetBytes(first));

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            var read = await _first.ReadAsync(buffer, cancellationToken);
            if (read > 0)
            {
                return read;
            }

            await Task.Delay(Timeout.Infinite, cancellationToken);
            return 0;
        }

        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
