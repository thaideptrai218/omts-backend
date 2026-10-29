using System.Net;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using OMTS.Application.Common.Models;
using OMTS.Infrastructure.Zalo;

namespace OMTS.Api.IntegrationTests.Auth;

/// <summary>The real Zalo Open API adapter against a stubbed graph.zalo.me.</summary>
public class GraphZaloOpenApiTests
{
    [Fact]
    public async Task Location_is_exchanged_with_headers_and_decimal_coordinates()
    {
        var api = Api(request =>
        {
            Assert.Equal("/v2.0/me/info", request.RequestUri!.AbsolutePath);
            Assert.Equal("access-location", Header(request, "access_token"));
            Assert.Equal("location-token", Header(request, "code"));
            Assert.Equal(_appSecret, Header(request, "secret_key"));
            Assert.Equal("", request.RequestUri.Query);
            return Json("""{"data":{"latitude":"21.123456","longitude":"107.456789","provider":"gps"},"error":0}""");
        });
        var position = await api.GetLocationAsync("access-location", "location-token", CancellationToken.None);
        Assert.Equal(ZaloLookupOutcome.Found, position.Outcome);
        Assert.Equal(21.123456m, position.Latitude);
        Assert.Equal(107.456789m, position.Longitude);
    }

    [Theory]
    [InlineData("{\"data\":{\"latitude\":\"91\",\"longitude\":\"107\"},\"error\":0}", ZaloLookupOutcome.Rejected)]
    [InlineData("{\"data\":{},\"error\":0}", ZaloLookupOutcome.Rejected)]
    [InlineData("{\"error\":117}", ZaloLookupOutcome.Unavailable)]
    [InlineData("{\"error\":-201}", ZaloLookupOutcome.Rejected)]
    public async Task Location_refusals_and_missing_coordinates_are_not_success(string response, ZaloLookupOutcome expected)
    {
        var result = await Api(_ => Json(response)).GetLocationAsync("access", "location", CancellationToken.None);
        Assert.Equal(expected, result.Outcome);
        Assert.Null(result.Latitude);
        Assert.Null(result.Longitude);
    }

    // Generated per run so no key-like literal is committed (gitleaks).
    private readonly string _appSecret = Convert.ToBase64String(RandomNumberGenerator.GetBytes(24));

    [Fact]
    public async Task User_id_request_is_signed_with_the_app_secret()
    {
        HttpRequestMessage? sent = null;
        var api = Api(request =>
        {
            sent = request;
            return Json("""{ "id": "3681046936240438345", "error": 0, "message": "Success" }""");
        });

        var lookup = await api.GetUserIdAsync("access-1", CancellationToken.None);

        Assert.Equal(ZaloLookup.Found("3681046936240438345"), lookup);
        Assert.Equal("/v2.0/me", sent!.RequestUri!.AbsolutePath);
        Assert.Equal("access-1", Header(sent, "access_token"));
        var proof = Convert.ToHexString(HMACSHA256.HashData(Encoding.UTF8.GetBytes(_appSecret), "access-1"u8.ToArray())).ToLowerInvariant();
        Assert.Equal(proof, Header(sent, "appsecret_proof"));
    }

    [Fact]
    public async Task Phone_token_is_exchanged_with_the_secret_key()
    {
        HttpRequestMessage? sent = null;
        var api = Api(request =>
        {
            sent = request;
            return Json("""{ "data": { "number": "84912345678" }, "error": 0, "message": "Success" }""");
        });

        var lookup = await api.GetPhoneNumberAsync("access-1", "phone-1", CancellationToken.None);

        Assert.Equal(ZaloLookup.Found("84912345678"), lookup);
        Assert.Equal("/v2.0/me/info", sent!.RequestUri!.AbsolutePath);
        Assert.Equal("phone-1", Header(sent, "code"));
        Assert.Equal(_appSecret, Header(sent, "secret_key"));
    }

    [Theory]
    [InlineData(115, ZaloLookupOutcome.Rejected)] // invalid code
    [InlineData(119, ZaloLookupOutcome.Rejected)] // code already used
    [InlineData(117, ZaloLookupOutcome.Unavailable)] // wrong secret key: this server's configuration
    public async Task Zalo_errors_on_the_phone_token(int error, ZaloLookupOutcome expected)
    {
        var api = Api(_ => Json($$"""{ "error": {{error}}, "message": "x" }"""));

        Assert.Equal(expected, (await api.GetPhoneNumberAsync("access-1", "phone-1", CancellationToken.None)).Outcome);
    }

    [Fact]
    public async Task Refused_access_token_and_unreachable_zalo_are_told_apart()
    {
        var refused = Api(_ => Json("""{ "error": -124, "message": "Access token is invalid" }"""));
        var down = Api(_ => throw new HttpRequestException("connection refused"));
        var failing = Api(_ => new HttpResponseMessage(HttpStatusCode.BadGateway));

        Assert.Equal(ZaloLookupOutcome.Rejected, (await refused.GetUserIdAsync("access-1", CancellationToken.None)).Outcome);
        Assert.Equal(ZaloLookupOutcome.Unavailable, (await down.GetUserIdAsync("access-1", CancellationToken.None)).Outcome);
        Assert.Equal(ZaloLookupOutcome.Unavailable, (await failing.GetUserIdAsync("access-1", CancellationToken.None)).Outcome);
    }

    private GraphZaloOpenApi Api(Func<HttpRequestMessage, HttpResponseMessage> answer)
    {
        var options = new ZaloOptions { Provider = ZaloOptions.GraphProvider, AppSecret = _appSecret };
        var http = new HttpClient(new StubHandler(answer)) { BaseAddress = new Uri(options.BaseUrl) };
        return new GraphZaloOpenApi(http, Options.Create(options), NullLogger<GraphZaloOpenApi>.Instance);
    }

    private static HttpResponseMessage Json(string body) =>
        new(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    private static string? Header(HttpRequestMessage request, string name) =>
        request.Headers.TryGetValues(name, out var values) ? values.Single() : null;

    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> answer) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(answer(request));
    }
}
