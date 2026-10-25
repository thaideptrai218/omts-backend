using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using OMTS.Api.IntegrationTests.Infrastructure;

namespace OMTS.Api.IntegrationTests.Farming;

/// <summary>
/// FARM-03 Swagger (OMTS-214): the multipart license upload and replace forms show every field with an example,
/// taken from the XML comments of the request type and the controller.
/// </summary>
public class ZoneLicenseSwaggerTests(AuthApiFactory factory) : IClassFixture<AuthApiFactory>
{
    [Theory]
    [InlineData("/api/zone-licenses", "post")]
    [InlineData("/api/zone-licenses/{id}/replace", "post")]
    [InlineData("/api/zone-licenses/{id}", "put")]
    public async Task License_forms_carry_field_examples_and_descriptions(string path, string method)
    {
        var response = await factory.CreateClient().GetAsync("/swagger/v1/swagger.json");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var operation = (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("paths").GetProperty(path).GetProperty(method);
        var form = operation.GetProperty("requestBody").GetProperty("content").GetProperty("multipart/form-data").GetProperty("schema").GetProperty("properties");

        Assert.Equal("AQUACULTURE_LICENSE", form.GetProperty("documentType").GetProperty("example").GetString());
        Assert.Equal("125/GP-UBND", form.GetProperty("documentNumber").GetProperty("example").GetString());
        Assert.Equal("2026-01-15", form.GetProperty("effectiveDate").GetProperty("example").GetString());
        Assert.Equal("2031-01-14", form.GetProperty("expiryDate").GetProperty("example").GetString());
        Assert.Equal("UBND tỉnh Quảng Ninh", form.GetProperty("issuingAuthority").GetProperty("example").GetString());
        Assert.Equal("3fa85f64-5717-4562-b3fc-2c963f66afa6", form.GetProperty("zoneIds").GetProperty("example")[0].GetString());
        Assert.Contains("repeat the", form.GetProperty("zoneIds").GetProperty("description").GetString());
        Assert.Equal("binary", form.GetProperty("file").GetProperty("format").GetString());
        Assert.False(string.IsNullOrWhiteSpace(operation.GetProperty("summary").GetString()));
    }
}
