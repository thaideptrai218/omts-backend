using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using OMTS.Api.IntegrationTests.Infrastructure;

namespace OMTS.Api.IntegrationTests.Labor;

/// <summary>
/// Definition of Done "Swagger documents every endpoint with examples". PENDING: the examples live in XML comments that
/// are not fed to Swagger (no GenerateDocumentationFile / IncludeXmlComments), so the document has no summary or example.
/// </summary>
public class LaborSwaggerExampleTests(AuthApiFactory factory) : IClassFixture<AuthApiFactory>
{
    [Fact]
    public async Task Labor_write_endpoints_carry_a_description_with_an_example_in_swagger()
    {
        var response = await factory.CreateClient().GetAsync("/swagger/v1/swagger.json");
        var paths = (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("paths");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        foreach (var path in new[] { "/api/labor/workers", "/api/zones/{zoneId}/work-assignments", "/api/pay-rates", "/api/payrolls", "/api/salary-advances" })
        {
            var operation = paths.GetProperty(path).GetProperty("post");
            var text = string.Join(" ", new[] { "summary", "description" }
                .Where(p => operation.TryGetProperty(p, out _)).Select(p => operation.GetProperty(p).GetString()));
            Assert.True(text.Contains("Example", StringComparison.OrdinalIgnoreCase) || operation.ToString().Contains("\"example\""), $"POST {path}: {operation}");
        }
    }
}
