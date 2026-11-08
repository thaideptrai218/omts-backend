using System.Net;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using OMTS.Api.IntegrationTests.Infrastructure;

namespace OMTS.Api.IntegrationTests.Platform;

/// <summary>OMTS-213: the API as the Docker image runs it (plain HTTP on 8080, browser calls from the web app).</summary>
public class LocalEnvironmentTests(AuthApiFactory factory) : IClassFixture<AuthApiFactory>
{
    private const string WebOrigin = "http://localhost:3000";
    private const string ZaloMiniAppOrigin = "https://h5.zdn.vn";
    private static readonly WebApplicationFactoryClientOptions NoRedirects = new() { AllowAutoRedirect = false };

    // The image listens on plain HTTP only: without an HTTPS port nothing is redirected.
    [Fact]
    public async Task Plain_http_is_served_as_is_when_no_https_port_is_configured()
    {
        var response = await factory.CreateClient(NoRedirects).GetAsync("/health");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    // Counter-check, so the test above proves something: once an HTTPS port is known, HTTP is redirected as before.
    [Fact]
    public async Task Plain_http_is_redirected_once_an_https_port_is_configured()
    {
        await using var https = factory.WithWebHostBuilder(b => b.UseSetting("https_port", "443"));

        var response = await https.CreateClient(NoRedirects).GetAsync("/health");

        Assert.Equal(HttpStatusCode.TemporaryRedirect, response.StatusCode);
        Assert.Equal("https", response.Headers.Location?.Scheme);
    }

    // Development allows the Next.js dev server; the preflight is answered without a token and without credentials.
    [Fact]
    public async Task The_web_app_origin_passes_the_cors_preflight_in_development()
    {
        var response = await factory.CreateClient().SendAsync(Preflight(WebOrigin));

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        Assert.Equal(WebOrigin, Header(response, "Access-Control-Allow-Origin"));
        Assert.Contains("authorization", Header(response, "Access-Control-Allow-Headers"), StringComparison.OrdinalIgnoreCase);
        Assert.Null(Header(response, "Access-Control-Allow-Credentials"));
    }

    // Zalo serves every deployed version of the Mini App from h5.zdn.vn; the answer names that one origin only.
    [Fact]
    public async Task The_zalo_mini_app_origin_passes_the_cors_preflight_in_development()
    {
        var response = await factory.CreateClient().SendAsync(Preflight(ZaloMiniAppOrigin));

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        Assert.Equal(ZaloMiniAppOrigin, Header(response, "Access-Control-Allow-Origin"));
        Assert.Contains("authorization", Header(response, "Access-Control-Allow-Headers"), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task A_cross_origin_response_names_the_web_app_and_exposes_the_export_file_name()
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, "/health");
        request.Headers.Add("Origin", WebOrigin);

        var response = await factory.CreateClient().SendAsync(request);

        Assert.Equal(WebOrigin, Header(response, "Access-Control-Allow-Origin"));
        Assert.Equal("Content-Disposition", Header(response, "Access-Control-Expose-Headers"));
    }

    [Fact]
    public async Task Other_origins_get_no_cors_headers()
    {
        var response = await factory.CreateClient().SendAsync(Preflight("http://other.example"));

        Assert.Null(Header(response, "Access-Control-Allow-Origin"));
    }

    // Production configures no origin, so there is no CORS policy at all.
    [Fact]
    public async Task Without_configured_origins_no_origin_is_allowed()
    {
        await using var production = factory.WithWebHostBuilder(b => b.UseEnvironment("Production"));

        var response = await production.CreateClient().SendAsync(Preflight(WebOrigin));

        Assert.Null(Header(response, "Access-Control-Allow-Origin"));
    }

    private static HttpRequestMessage Preflight(string origin)
    {
        var request = new HttpRequestMessage(HttpMethod.Options, "/api/me");
        request.Headers.Add("Origin", origin);
        request.Headers.Add("Access-Control-Request-Method", "GET");
        request.Headers.Add("Access-Control-Request-Headers", "authorization");
        return request;
    }

    private static string? Header(HttpResponseMessage response, string name) =>
        response.Headers.TryGetValues(name, out var values) ? string.Join(",", values) : null;
}
