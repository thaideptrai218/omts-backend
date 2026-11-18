using System.Net;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace OMTS.Infrastructure.AddressLookup;

/// <summary>
/// One GET of a JSON answer for the lookup adapters. Failures (no connection, timeout, an answer that is not JSON, a status other than success) come back as a
/// status with no document, never as an exception. Only the service, the status and the kind of failure are logged: never the tax code of the URL and never the answer.
/// </summary>
internal static class LookupHttp
{
    public static async Task<(HttpStatusCode? Status, JsonDocument? Body)> GetJsonAsync(
        HttpClient http, string relativeUrl, string service, ILogger logger, CancellationToken cancellationToken)
    {
        try
        {
            using var response = await http.GetAsync(relativeUrl, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                if (response.StatusCode != HttpStatusCode.NotFound)
                {
                    logger.LogWarning("{Service} answered HTTP {Status}", service, (int)response.StatusCode);
                }

                return (response.StatusCode, null);
            }

            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
            return (response.StatusCode, await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken));
        }
        catch (Exception exception) when (exception is HttpRequestException or JsonException or InvalidOperationException
            || (exception is TaskCanceledException && !cancellationToken.IsCancellationRequested))
        {
            // The message of a failed request may carry the URL, so only the type of the failure is logged.
            logger.LogWarning("{Service} did not answer ({Failure})", service, exception.GetType().Name);
            return (null, null);
        }
    }
}
