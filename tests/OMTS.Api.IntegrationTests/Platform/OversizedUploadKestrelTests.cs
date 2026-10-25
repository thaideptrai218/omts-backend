using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using OMTS.Api.IntegrationTests.Cooperatives;
using OMTS.Api.IntegrationTests.Infrastructure;

namespace OMTS.Api.IntegrationTests.Platform;

/// <summary>
/// A file above an endpoint's request size limit, on a real Kestrel host (TestServer has no body size limit):
/// 400 MSG01 naming the file field, never a bare 413 or a 500.
/// </summary>
public class OversizedUploadKestrelTests
{
    [Theory]
    // With Expect: 100-continue the client never sends the body. Without it (browsers) the body is sent, and
    // Kestrel discards it (up to twice the limit) so the client still reads the answer.
    [InlineData(true)]
    [InlineData(false)]
    public async Task Logo_above_the_limit_is_400_MSG01_on_the_file_field(bool expectContinue)
    {
        await using var api = new KestrelApiFactory();
        await api.EnsureCooperativeAsync();
        await api.AddAccountAsync("owner", AcsTestKit.Password);
        using var client = await LoginOnKestrelAsync(api, "owner");

        using var response = await UploadAsync(client, "/api/cooperative/profile/logo", "file", 4 * 1024 * 1024, expectContinue);

        await AssertInvalidFileAsync(response, "file");
    }

    [Fact]
    public async Task Logo_under_the_limit_is_still_read_and_saved_on_Kestrel()
    {
        await using var api = new KestrelApiFactory();
        await api.EnsureCooperativeAsync();
        await api.AddAccountAsync("owner", AcsTestKit.Password);
        using var client = await LoginOnKestrelAsync(api, "owner");
        var logo = new byte[1024 * 1024];
        CooperativeProfileRequests.Png.CopyTo(logo, 0);

        using var response = await UploadAsync(client, "/api/cooperative/profile/logo", "file", logo, expectContinue: false);

        Assert.True(response.StatusCode == HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task License_file_above_the_limit_is_400_MSG01_on_the_file_field()
    {
        await using var api = new KestrelApiFactory();
        await api.AddAccountAsync("owner", AcsTestKit.Password);
        using var client = await LoginOnKestrelAsync(api, "owner");

        using var response = await UploadAsync(client, "/api/zone-licenses", "file", 12 * 1024 * 1024, expectContinue: true);

        await AssertInvalidFileAsync(response, "file");
    }

    [Fact]
    public async Task Water_meter_photo_above_the_limit_names_the_photo_field()
    {
        await using var api = new KestrelApiFactory();
        await api.AddAccountAsync("owner", AcsTestKit.Password);
        using var client = await LoginOnKestrelAsync(api, "owner");

        using var response = await UploadAsync(client, $"/api/zones/{Guid.NewGuid()}/water-measurements/recognize", "photo", 13 * 1024 * 1024, expectContinue: true);

        await AssertInvalidFileAsync(response, "photo");
    }

    private static async Task<HttpClient> LoginOnKestrelAsync(KestrelApiFactory api, string username)
    {
        var client = api.CreateKestrelClient();
        var tokens = await AcsTestKit.LoginTokensAsync(client, username);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", tokens.GetProperty("accessToken").GetString());
        return client;
    }

    private static Task<HttpResponseMessage> UploadAsync(HttpClient client, string path, string field, int bytes, bool expectContinue) =>
        UploadAsync(client, path, field, new byte[bytes], expectContinue);

    private static async Task<HttpResponseMessage> UploadAsync(HttpClient client, string path, string field, byte[] file, bool expectContinue)
    {
        var content = new MultipartFormDataContent { { new ByteArrayContent(file), field, "upload.png" } };
        var request = new HttpRequestMessage(HttpMethod.Post, path) { Content = content };
        request.Headers.ExpectContinue = expectContinue;
        return await client.SendAsync(request);
    }

    private static async Task AssertInvalidFileAsync(HttpResponseMessage response, string field)
    {
        var text = await response.Content.ReadAsStringAsync();
        Assert.True(response.StatusCode == HttpStatusCode.BadRequest, $"HTTP {(int)response.StatusCode}: {text}");
        var body = JsonDocument.Parse(text).RootElement;
        Assert.Equal("MSG01", body.GetProperty("code").GetString());
        Assert.Equal(field, body.GetProperty("field").GetString());
    }
}
