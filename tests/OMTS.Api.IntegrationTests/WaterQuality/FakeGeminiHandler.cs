using System.Collections.Concurrent;
using System.Net;

namespace OMTS.Api.IntegrationTests.WaterQuality;

/// <summary>A request the fake Gemini endpoint received: address, key header and the JSON body.</summary>
public sealed record GeminiRequest(string Uri, string? ApiKeyHeader, string Body);

/// <summary>
/// Stands in for the Google endpoint so no test reaches the network. <paramref name="answer"/> gets the 1-based call number;
/// <paramref name="delay"/> is waited first (cancelled with the request, like a slow server).
/// </summary>
public sealed class FakeGeminiHandler(Func<int, HttpResponseMessage> answer, TimeSpan? delay = null) : HttpMessageHandler
{
    private readonly ConcurrentQueue<GeminiRequest> _requests = new();

    public IReadOnlyCollection<GeminiRequest> Requests => _requests;

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var body = request.Content is null ? "" : await request.Content.ReadAsStringAsync(cancellationToken);
        _requests.Enqueue(new GeminiRequest(
            request.RequestUri!.ToString(),
            request.Headers.TryGetValues("x-goog-api-key", out var values) ? values.Single() : null,
            body));
        if (delay is { } wait)
        {
            await Task.Delay(wait, cancellationToken);
        }

        return answer(_requests.Count);
    }
}
