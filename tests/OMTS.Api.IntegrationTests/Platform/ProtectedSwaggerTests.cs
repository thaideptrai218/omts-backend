using System.Net;
using System.Net.Http.Headers;
using System.Text;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using OMTS.Api.Extensions;
using OMTS.Api.IntegrationTests.Infrastructure;

namespace OMTS.Api.IntegrationTests.Platform;

/// <summary>
/// Swagger outside Development (council decision 4, OMTS-216, kept by StagingEndpointsAndFakeProvidersTests: off by
/// default): with Swagger:Enabled it is served only behind its own Basic login, and turned on without one the host
/// does not start. Credentials are generated per run (gitleaks).
/// </summary>
public class ProtectedSwaggerTests
{
    private static readonly string[] SwaggerPaths =
    [
        "/swagger", "/swagger/", "/swagger/index.html", "/SWAGGER/INDEX.HTML", "/Swagger/Index.html", "/swagger/index.js",
        "/swagger/swagger-ui.css", "/swagger/v1/swagger.json", "/SWAGGER/V1/SWAGGER.JSON", "/swagger/v1/swagger.yaml",
        "/%73wagger/index.html",
    ];

    // Swashbuckle's own UI matcher also answers these (optional "/", unescaped "."): never served around the login.
    private static readonly string[] LookAlikePaths = ["/swaggerindex.html", "/SWAGGERINDEX.HTML", "/swaggerindex-html", "/swagger.json"];

    [Theory]
    [InlineData("Staging")]
    [InlineData("Production")]
    public async Task Enabled_swagger_answers_401_with_a_basic_challenge_without_the_right_credentials(string environment)
    {
        var (user, password) = (AcsTestKit.NewCode(), AcsTestKit.NewPassword() + AcsTestKit.NewPassword());
        await using var host = Host(environment, enabled: "true", user, password);
        var client = host.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

        foreach (var path in SwaggerPaths)
        {
            foreach (var authorization in new AuthenticationHeaderValue?[]
                     {
                         null,
                         Basic(user, password + "x"),
                         Basic(user.ToUpperInvariant() + "x", password),
                         new("Basic", "not base64!"),
                         new("Bearer", password),
                     })
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, path);
                request.Headers.Authorization = authorization;
                using var response = await client.SendAsync(request);

                Assert.True(response.StatusCode == HttpStatusCode.Unauthorized, $"{path} {authorization?.Scheme}: {(int)response.StatusCode}");
                Assert.Equal("Basic", response.Headers.WwwAuthenticate.Single().Scheme);
                Assert.True(response.Headers.CacheControl?.NoStore, path);
                Assert.DoesNotContain("openapi", await response.Content.ReadAsStringAsync(), StringComparison.OrdinalIgnoreCase);
            }
        }
    }

    [Fact]
    public async Task Look_alike_paths_that_swashbuckle_would_match_are_not_served_without_the_login()
    {
        var (user, password) = (AcsTestKit.NewCode(), AcsTestKit.NewPassword() + AcsTestKit.NewPassword());
        await using var host = Host("Staging", enabled: "true", user, password);
        var client = host.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

        foreach (var path in LookAlikePaths)
        {
            using var response = await client.GetAsync(path);
            var body = await response.Content.ReadAsStringAsync();
            Assert.True(response.StatusCode == HttpStatusCode.NotFound, $"{path}: {(int)response.StatusCode}");
            Assert.DoesNotContain("swagger-ui", body, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Theory]
    [InlineData("Staging")]
    [InlineData("Production")]
    public async Task Enabled_swagger_serves_the_page_and_the_document_with_the_right_credentials(string environment)
    {
        var (user, password) = (AcsTestKit.NewCode(), AcsTestKit.NewPassword() + AcsTestKit.NewPassword());
        await using var host = Host(environment, enabled: "true", user, password);
        var client = host.CreateClient();
        client.DefaultRequestHeaders.Authorization = Basic(user, password);

        var page = await client.GetAsync("/swagger/index.html");
        var document = await client.GetAsync("/swagger/v1/swagger.json");

        Assert.Equal(HttpStatusCode.OK, page.StatusCode);
        Assert.Equal(HttpStatusCode.OK, document.StatusCode);
        Assert.Contains("\"openapi\"", await document.Content.ReadAsStringAsync());
        Assert.True(document.Headers.CacheControl?.NoStore);
    }

    // The Basic login opens the documentation only: the API keeps asking for its bearer token, and the dev inbox stays off.
    [Fact]
    public async Task The_swagger_login_opens_neither_the_api_nor_the_dev_inbox()
    {
        var (user, password) = (AcsTestKit.NewCode(), AcsTestKit.NewPassword() + AcsTestKit.NewPassword());
        await using var host = Host("Staging", enabled: "true", user, password);
        var client = host.CreateClient();
        client.DefaultRequestHeaders.Authorization = Basic(user, password);

        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/me")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync("/api/dev/sms")).StatusCode);
    }

    [Theory]
    [InlineData("false")]
    [InlineData("")]
    public async Task Swagger_stays_404_outside_development_unless_enabled(string enabled)
    {
        await using var host = Host("Staging", enabled, AcsTestKit.NewCode(), AcsTestKit.NewPassword() + AcsTestKit.NewPassword());
        var client = host.CreateClient();

        foreach (var path in SwaggerPaths)
        {
            Assert.True((await client.GetAsync(path)).StatusCode == HttpStatusCode.NotFound, path);
        }
    }

    // Passwords built per run: long enough (16+), or one character short.
    public static TheoryData<string, string> MissingCredentials => new()
    {
        { "", "" },
        { "docs", "" },
        { "", AcsTestKit.NewPassword() + AcsTestKit.NewPassword() },
        { "docs", (AcsTestKit.NewPassword() + AcsTestKit.NewPassword())[..(SwaggerPipeline.MinimumPasswordLength - 1)] },
        { "do:cs", AcsTestKit.NewPassword() + AcsTestKit.NewPassword() },
    };

    [Theory]
    [MemberData(nameof(MissingCredentials))]
    public void Enabled_without_usable_credentials_the_host_refuses_to_start_and_names_the_settings(string user, string password)
    {
        using var host = Host("Staging", enabled: "true", user, password);

        var error = Assert.Throws<InvalidOperationException>(() => host.CreateClient());

        Assert.Contains(SwaggerPipeline.UsernameKey, error.Message);
        Assert.Contains(SwaggerPipeline.PasswordKey, error.Message);
        if (password.Length > 0)
        {
            Assert.DoesNotContain(password, error.Message);
        }
    }

    // Development keeps its open Swagger whatever the settings say.
    [Fact]
    public async Task Development_serves_swagger_without_a_login()
    {
        await using var host = Host("Development", enabled: "true", "", "");
        Assert.Equal(HttpStatusCode.OK, (await host.CreateClient().GetAsync("/swagger/v1/swagger.json")).StatusCode);
    }

    private static WebApplicationFactory<Program> Host(string environment, string enabled, string user, string password) =>
        new AuthApiFactory().WithWebHostBuilder(b => b
            .UseEnvironment(environment)
            .UseSetting(SwaggerPipeline.EnabledKey, enabled)
            .UseSetting(SwaggerPipeline.UsernameKey, user)
            .UseSetting(SwaggerPipeline.PasswordKey, password));

    private static AuthenticationHeaderValue Basic(string user, string password) =>
        new("Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes($"{user}:{password}")));
}
