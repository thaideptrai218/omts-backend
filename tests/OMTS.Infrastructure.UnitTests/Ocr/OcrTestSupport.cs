using System.Collections.Concurrent;
using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using OMTS.Application.Common.Interfaces.Ocr;

namespace OMTS.Infrastructure.UnitTests.Ocr;

/// <summary>A request the fake endpoint received: address, Authorization header and the JSON body.</summary>
internal sealed record CapturedRequest(string Uri, string? Authorization, string Body);

/// <summary>Stands in for the provider endpoint so no test reaches the network. <c>answer</c> gets the 1-based call number.</summary>
internal sealed class FakeHandler(Func<int, HttpResponseMessage> answer, TimeSpan? delay = null) : HttpMessageHandler
{
    private readonly ConcurrentQueue<CapturedRequest> _requests = new();

    public IReadOnlyCollection<CapturedRequest> Requests => _requests;

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var body = request.Content is null ? "" : await request.Content.ReadAsStringAsync(cancellationToken);
        _requests.Enqueue(new CapturedRequest(request.RequestUri!.ToString(), request.Headers.Authorization?.ToString(), body));
        if (delay is { } wait)
        {
            await Task.Delay(wait, cancellationToken);
        }

        return answer(_requests.Count);
    }
}

/// <summary>Keeps every formatted log line, to prove secrets never reach the logs.</summary>
internal sealed class CapturingLoggerProvider : ILoggerProvider
{
    private readonly ConcurrentQueue<string> _lines = new();

    public IReadOnlyCollection<string> Lines => _lines;

    public ILogger CreateLogger(string categoryName) => new CapturingLogger(_lines);

    public void Dispose()
    {
    }

    private sealed class CapturingLogger(ConcurrentQueue<string> lines) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
            lines.Enqueue($"{logLevel}: {formatter(state, exception)} {exception}");
    }
}

/// <summary>A clock that only moves when a test says so; its timers are the real ones.</summary>
internal sealed class ManualClock : TimeProvider
{
    private long _ticks;

    public override long TimestampFrequency => 1000;

    public override long GetTimestamp() => Interlocked.Read(ref _ticks);

    public void Advance(TimeSpan time) => Interlocked.Add(ref _ticks, (long)time.TotalMilliseconds);
}

/// <summary>Builds provider answers.</summary>
internal static class Answers
{
    public static HttpResponseMessage Sse(params string[] chunks) =>
        new(HttpStatusCode.OK) { Content = new StringContent(string.Concat(chunks.Select(c => $"data: {c}\n\n")), Encoding.UTF8, "text/event-stream") };

    public static HttpResponseMessage Json(HttpStatusCode status, string body) =>
        new(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    /// <summary>A streamed content chunk: <c>choices[0].delta.content</c>.</summary>
    public static string Delta(string content, string? finishReason = null, string? reasoning = null) =>
        JsonSerializer.Serialize(new
        {
            choices = new[] { new { index = 0, delta = new { content, reasoning_content = reasoning }, finish_reason = finishReason } },
        });

    public static string Usage(int prompt, int completion, int reasoning) =>
        JsonSerializer.Serialize(new { choices = Array.Empty<object>(), usage = new { prompt_tokens = prompt, completion_tokens = completion, completion_tokens_details = new { reasoning_tokens = reasoning } } });

    /// <summary>The whole answer of a non-streamed call.</summary>
    public static string Body(string content, string finishReason = "stop") =>
        JsonSerializer.Serialize(new
        {
            choices = new[] { new { index = 0, message = new { role = "assistant", content }, finish_reason = finishReason } },
            usage = new { prompt_tokens = 900, completion_tokens = 30, completion_tokens_details = new { reasoning_tokens = 0 } },
        });

    public static HttpResponseMessage GoodStream(string json = """{"a":"7.8"}""") =>
        Sse(Delta(json[..4]), Delta(json[4..], "stop"), Usage(1200, 40, 7), "[DONE]");
}

/// <summary>A scripted client for the fallback tests: answers what it is told and counts its calls.</summary>
internal sealed class ScriptedClient(Func<int, GeminiJsonResult> answer) : IGeminiVisionClient
{
    public int Calls { get; private set; }

    public Task<GeminiJsonResult> GenerateJsonAsync(
        GeminiReader reader, ReadOnlyMemory<byte> image, string mimeType, string prompt, JsonElement responseSchema, CancellationToken cancellationToken)
    {
        Calls++;
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(answer(Calls));
    }

    public static GeminiJsonResult Good() => new(true, JsonSerializer.Deserialize<JsonElement>("""{"a":"1"}"""), null, null, TimeSpan.Zero);

    public static GeminiJsonResult Failed(string code) => GeminiJsonResult.Failure(code, TimeSpan.Zero);
}
