using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using OMTS.Api.IntegrationTests.Infrastructure;
using static OMTS.Api.IntegrationTests.Infrastructure.AcsTestKit;

namespace OMTS.Api.IntegrationTests.Cooperatives;

/// <summary>
/// QA of OMTS-217 PR 0.7 on a real Kestrel host: a cover photo or certificate above the request size limit gets the
/// same 400 MSG-GEN-01 on the endpoint's own field as one refused by its file rules (OMTS-214 convention), not a 413.
/// </summary>
public class CooperativeUploadLimitQaTests(KestrelApiFactory api) : IClassFixture<KestrelApiFactory>
{
    private const long Megabyte = 1024 * 1024;

    // Sent with Expect: 100-continue, so the bodies are never built in memory nor sent.
    [Theory]
    [InlineData("api/cooperative/profile/cover-photo", "Cover photo", 7)]
    [InlineData("api/cooperative/certifications", "File", 12)]
    [InlineData("api/cooperative/certifications/{0}", "File", 12)]
    public async Task Upload_above_the_request_limit_is_400_msg_gen_01_on_its_field(string path, string field, int megabytes)
    {
        await api.EnsureCooperativeAsync();
        var password = NewPassword();
        var owner = await api.AddAccountAsync(NewCode(), password);
        using var client = KestrelClient();
        var tokens = await LoginTokensAsync(client, owner.Username, password);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", tokens.GetProperty("accessToken").GetString());
        var url = string.Format(path, Guid.NewGuid());
        var method = url.EndsWith("certifications", StringComparison.Ordinal) || url.EndsWith("cover-photo", StringComparison.Ordinal) ? HttpMethod.Post : HttpMethod.Put;
        var request = new HttpRequestMessage(method, url)
        {
            Content = new MultipartFormDataContent { { new ZeroContent(megabytes * Megabyte), "file", "upload.png" } },
        };
        request.Headers.ExpectContinue = true;

        using var response = await client.SendAsync(request);

        var text = await response.Content.ReadAsStringAsync();
        Assert.True(response.StatusCode == HttpStatusCode.BadRequest, $"HTTP {(int)response.StatusCode}: {text}");
        var problem = JsonDocument.Parse(text).RootElement;
        Assert.Equal(("MSG-GEN-01", field), (problem.GetProperty("code").GetString(), problem.GetProperty("field").GetString()));
    }

    // HttpClient sends the body anyway when no 100 Continue arrives in time; a long wait keeps the refused body unsent.
    private HttpClient KestrelClient()
    {
        using var probe = api.CreateKestrelClient();
        return new HttpClient(new SocketsHttpHandler { Expect100ContinueTimeout = TimeSpan.FromMinutes(2) }) { BaseAddress = probe.BaseAddress };
    }

    /// <summary>Zeros written on demand: a large body costs no memory, and nothing at all when it is never sent.</summary>
    private sealed class ZeroContent(long length) : HttpContent
    {
        protected override async Task SerializeToStreamAsync(Stream stream, System.Net.TransportContext? context)
        {
            var buffer = new byte[81920];
            for (var left = length; left > 0; left -= buffer.Length)
            {
                await stream.WriteAsync(buffer.AsMemory(0, (int)Math.Min(buffer.Length, left)));
            }
        }

        protected override bool TryComputeLength(out long size)
        {
            size = length;
            return true;
        }
    }
}
