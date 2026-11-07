using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using OMTS.Api.IntegrationTests.Infrastructure;

namespace OMTS.Api.IntegrationTests.Platform;

/// <summary>
/// QA of the OMTS-214 Swagger changes outside the license forms: parameter
/// names stay as they were before the change, so the web's generated clients do not change.
/// </summary>
public class SwaggerQaTests(AuthApiFactory factory) : IClassFixture<AuthApiFactory>
{
    [Fact]
    public async Task Query_parameter_names_are_unchanged()
    {
        var response = await factory.CreateClient().GetAsync("/swagger/v1/swagger.json");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var paths = (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("paths");

        // Query fields of a [FromQuery] record keep their property names, as on develop.
        var receiptFilters = paths.GetProperty("/api/purchase-receipts").GetProperty("get").GetProperty("parameters").EnumerateArray()
            .Select(p => p.GetProperty("name").GetString()!).ToList();
        Assert.Contains("ItemGroup", receiptFilters);
        Assert.Contains("SupplierId", receiptFilters);
        Assert.Contains("PageSize", receiptFilters);
        Assert.DoesNotContain("itemGroup", receiptFilters);
        // Plain action parameters were already camelCase and stay so.
        var employeeFilters = paths.GetProperty("/api/employees").GetProperty("get").GetProperty("parameters").EnumerateArray()
            .Select(p => p.GetProperty("name").GetString()!).ToList();
        Assert.Contains("pageSize", employeeFilters);
        Assert.Contains("zoneId", employeeFilters);
    }
}
