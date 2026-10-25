using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using OMTS.Api.IntegrationTests.Infrastructure;

namespace OMTS.Api.IntegrationTests.Platform;

/// <summary>
/// QA of the OMTS-214 Swagger changes outside the license forms: the unlink endpoint is documented, and every
/// parameter is now described in camelCase (a document-only change; binding still ignores case).
/// </summary>
public class SwaggerQaTests(AuthApiFactory factory) : IClassFixture<AuthApiFactory>
{
    [Fact]
    public async Task Unlink_is_documented_and_query_parameters_are_described_in_camel_case()
    {
        var response = await factory.CreateClient().GetAsync("/swagger/v1/swagger.json");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var paths = (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("paths");

        var unlink = paths.GetProperty("/api/employees/{id}/account").GetProperty("delete");
        var rowVersion = Assert.Single(unlink.GetProperty("parameters").EnumerateArray(), p => p.GetProperty("name").GetString() == "rowVersion");
        Assert.Equal("query", rowVersion.GetProperty("in").GetString());
        Assert.Contains("URL-encoded", rowVersion.GetProperty("description").GetString());
        Assert.True(unlink.GetProperty("responses").TryGetProperty("200", out _));
        Assert.Contains("DELETE", paths.GetProperty("/api/employees/{id}").GetProperty("put").GetProperty("summary").GetString());

        var receiptFilters = paths.GetProperty("/api/purchase-receipts").GetProperty("get").GetProperty("parameters").EnumerateArray()
            .Select(p => p.GetProperty("name").GetString()!).ToList();
        Assert.Contains("itemGroup", receiptFilters);
        Assert.Contains("supplierId", receiptFilters);
        Assert.Contains("pageSize", receiptFilters);
        Assert.All(receiptFilters, name => Assert.True(char.IsLower(name[0]), name));
    }
}
