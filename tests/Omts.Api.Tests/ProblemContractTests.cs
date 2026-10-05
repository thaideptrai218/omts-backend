using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace Omts.Api.Tests;

public sealed class ProblemContractTests
{
    [Theory]
    [InlineData("application/json")]
    [InlineData("text/plain")]
    [InlineData("text/html")]
    public async Task UnknownRoutesReturnGenericProblem(string accept)
    {
        await using var factory = new ApiFactory();
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Accept.ParseAdd(accept);
        using var response = await client.GetAsync("/orders?secret=private-token");
        await AssertProblemAsync(response, HttpStatusCode.NotFound, "Not Found");
    }

    [Theory]
    [InlineData("application/json")]
    [InlineData("text/html")]
    [InlineData("text/plain")]
    public async Task ExceptionsReturnSafeProductionProblem(string accept)
    {
        await using var factory = new ApiFactory();
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Accept.ParseAdd(accept);
        using var response = await client.GetAsync("/__tests/exception?secret=private-token");
        await AssertProblemAsync(response, HttpStatusCode.InternalServerError, "Internal Server Error");
    }

    [Fact]
    public async Task RequestsExceedingTimeoutReturnGenericGatewayTimeout()
    {
        await using var factory = new ApiFactory(new Dictionary<string, string?>
        {
            ["Hosting:RequestTimeoutSeconds"] = "1"
        });
        using var client = factory.CreateClient();
        using var response = await client.GetAsync("/__tests/timeout");
        await AssertProblemAsync(response, HttpStatusCode.GatewayTimeout, "Gateway Timeout");
    }

    [Fact]
    public async Task OversizedRequestIsRejectedBeforeHealthHandler()
    {
        await using var factory = new ApiFactory(new Dictionary<string, string?>
        {
            ["Hosting:MaxRequestBodyBytes"] = "16"
        });
        using var client = factory.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Post, "/health/live")
        {
            Content = new ByteArrayContent(new byte[17])
        };
        using var response = await client.SendAsync(request);
        await AssertProblemAsync(response, HttpStatusCode.RequestEntityTooLarge, "Payload Too Large");
    }

    private static async Task AssertProblemAsync(HttpResponseMessage response, HttpStatusCode status, string title)
    {
        Assert.Equal(status, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        JsonElement problem = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal((int)status, problem.GetProperty("status").GetInt32());
        Assert.Equal("about:blank", problem.GetProperty("type").GetString());
        Assert.Equal(title, problem.GetProperty("title").GetString());
        Assert.False(string.IsNullOrWhiteSpace(problem.GetProperty("traceId").GetString()));
        Assert.Equal(4, problem.EnumerateObject().Count());
    }
}
