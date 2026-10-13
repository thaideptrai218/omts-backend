using System.Net;
using Microsoft.AspNetCore.Mvc.Testing;

namespace OMTS.Api.IntegrationTests;

public class HealthEndpointTests(WebApplicationFactory<Program> factory)
    : IClassFixture<WebApplicationFactory<Program>>
{
    // Azure and CI probe /health; it must respond without requiring a configured database.
    [Fact]
    public async Task Health_returns_healthy_without_database()
    {
        var client = factory.CreateClient();

        var response = await client.GetAsync("/health");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("Healthy", await response.Content.ReadAsStringAsync());
    }
}
