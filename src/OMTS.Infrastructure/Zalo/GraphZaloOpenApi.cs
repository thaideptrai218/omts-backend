using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using OMTS.Application.Common.Interfaces.Identity;
using OMTS.Application.Common.Models;

namespace OMTS.Infrastructure.Zalo;

/// <summary>
/// The real Zalo Open API (graph.zalo.me), as described for Zalo Mini Apps: <c>/v2.0/me</c> gives the user id of
/// an access token (signed with <c>appsecret_proof</c>), <c>/v2.0/me/info</c> exchanges a phone token for the
/// number. Zalo answers HTTP 200 with <c>error</c> ≠ 0 when it refuses a token. Tokens are never logged.
/// </summary>
internal sealed class GraphZaloOpenApi(HttpClient http, IOptions<ZaloOptions> options, ILogger<GraphZaloOpenApi> logger) : IZaloOpenApi
{
    // Phone-token errors that are configuration problems of this server, not the user's token:
    // 116 secret_key missing, 117 secret_key wrong.
    private static readonly int[] ConfigurationErrors = [116, 117];

    public async Task<ZaloLocationLookup> GetLocationAsync(string accessToken, string locationToken, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, "v2.0/me/info");
        request.Headers.TryAddWithoutValidation("access_token", accessToken);
        request.Headers.TryAddWithoutValidation("code", locationToken);
        request.Headers.TryAddWithoutValidation("secret_key", options.Value.AppSecret);
        using var body = await SendAsync(request, "location", cancellationToken);
        if (body is null) return ZaloLocationLookup.Unavailable;
        var root = body.RootElement;
        if (ErrorCode(root) is { } error and not 0)
        {
            return ConfigurationErrors.Contains(error) ? ZaloLocationLookup.Unavailable : ZaloLocationLookup.Rejected;
        }

        return root.TryGetProperty("data", out var data)
            && decimal.TryParse(Text(data, "latitude"), NumberStyles.Float, CultureInfo.InvariantCulture, out var lat)
            && decimal.TryParse(Text(data, "longitude"), NumberStyles.Float, CultureInfo.InvariantCulture, out var lng)
            && lat is >= -90 and <= 90 && lng is >= -180 and <= 180
            ? new ZaloLocationLookup(ZaloLookupOutcome.Found, lat, lng) : ZaloLocationLookup.Rejected;
    }

    public async Task<ZaloLookup> GetUserIdAsync(string accessToken, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, "v2.0/me?fields=id");
        request.Headers.TryAddWithoutValidation("access_token", accessToken);
        request.Headers.TryAddWithoutValidation("appsecret_proof", AppSecretProof(accessToken));

        using var body = await SendAsync(request, "user id", cancellationToken);
        if (body is null)
        {
            return ZaloLookup.Unavailable;
        }

        var root = body.RootElement;
        if (ErrorCode(root) is { } error and not 0)
        {
            logger.LogInformation("Zalo refused an access token (error {Error})", error);
            return ZaloLookup.Rejected;
        }

        return Text(root, "id") is { Length: > 0 } id ? ZaloLookup.Found(id) : ZaloLookup.Rejected;
    }

    public async Task<ZaloLookup> GetPhoneNumberAsync(string accessToken, string phoneToken, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, "v2.0/me/info");
        request.Headers.TryAddWithoutValidation("access_token", accessToken);
        request.Headers.TryAddWithoutValidation("code", phoneToken);
        request.Headers.TryAddWithoutValidation("secret_key", options.Value.AppSecret);

        using var body = await SendAsync(request, "phone number", cancellationToken);
        if (body is null)
        {
            return ZaloLookup.Unavailable;
        }

        var root = body.RootElement;
        if (ErrorCode(root) is { } error and not 0)
        {
            if (ConfigurationErrors.Contains(error))
            {
                logger.LogError("Zalo refused the App secret (error {Error}); check Zalo:AppSecret", error);
                return ZaloLookup.Unavailable;
            }

            logger.LogInformation("Zalo refused a phone token (error {Error})", error);
            return ZaloLookup.Rejected;
        }

        return root.TryGetProperty("data", out var data) && Text(data, "number") is { Length: > 0 } number
            ? ZaloLookup.Found(number)
            : ZaloLookup.Rejected;
    }

    /// <summary>The parsed answer, or null when Zalo could not be reached or did not answer with JSON.</summary>
    private async Task<JsonDocument?> SendAsync(HttpRequestMessage request, string what, CancellationToken cancellationToken)
    {
        try
        {
            using var response = await http.SendAsync(request, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                logger.LogWarning("Zalo Open API answered HTTP {Status} for the {What}", (int)response.StatusCode, what);
                return null;
            }

            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
            return await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
        }
        catch (Exception exception) when (exception is HttpRequestException or JsonException
            || (exception is TaskCanceledException && !cancellationToken.IsCancellationRequested))
        {
            logger.LogWarning(exception, "Zalo Open API did not answer for the {What}", what);
            return null;
        }
    }

    /// <summary>HMAC-SHA256 of the access token keyed with the App secret, in lowercase hex (required since 01/01/2024).</summary>
    private string AppSecretProof(string accessToken) =>
        Convert.ToHexString(HMACSHA256.HashData(Encoding.UTF8.GetBytes(options.Value.AppSecret), Encoding.UTF8.GetBytes(accessToken)))
            .ToLowerInvariant();

    private static int? ErrorCode(JsonElement root) =>
        root.TryGetProperty("error", out var error) && error.ValueKind == JsonValueKind.Number && error.TryGetInt32(out var code) ? code : null;

    // Ids and numbers come as strings; read a number too, in case Zalo sends one.
    private static string? Text(JsonElement element, string name) =>
        !element.TryGetProperty(name, out var value) ? null
            : value.ValueKind == JsonValueKind.String ? value.GetString()
            : value.ValueKind == JsonValueKind.Number ? value.GetRawText()
            : null;
}
