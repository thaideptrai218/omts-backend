using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using OMTS.Api.IntegrationTests.Farming;
using OMTS.Api.IntegrationTests.Infrastructure;
using static OMTS.Api.IntegrationTests.Farming.FarmingTestKit;

namespace OMTS.Api.IntegrationTests.Cultivation;

/// <summary>QA of PR C4 on a real Kestrel host, the only one that applies the request size limit.</summary>
public class CycleLogsQaTestsKestrel(KestrelApiFactory api) : IClassFixture<KestrelApiFactory>
{
    private const long Megabyte = 1024 * 1024;

    private async Task<HttpClient> OnKestrelAsync(string username)
    {
        using var probe = api.CreateKestrelClient();
        var client = new HttpClient(new SocketsHttpHandler { Expect100ContinueTimeout = TimeSpan.FromMinutes(2) }) { BaseAddress = probe.BaseAddress, Timeout = TimeSpan.FromMinutes(5) };
        var tokens = await AcsTestKit.LoginTokensAsync(client, username);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", tokens.GetProperty("accessToken").GetString());
        return client;
    }

    // The POST has this case in OversizedUploadQaTests; the PUT has the same limit and must answer the same.
    [Fact]
    public async Task A_correction_above_the_request_limit_answers_400_on_the_photos()
    {
        var setup = await api.CycleAsync();
        using var client = await OnKestrelAsync(setup.Owner.Account.Username);
        var request = new HttpRequestMessage(HttpMethod.Put, $"api/cultivation-logs/{Guid.NewGuid()}")
        {
            Content = new MultipartFormDataContent { { new ZeroContent(106 * Megabyte), "photos", "upload.jpg" } },
        };
        request.Headers.ExpectContinue = true;

        using var response = await client.SendAsync(request);

        var problem = JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;
        Assert.Equal((HttpStatusCode.BadRequest, "MSG-GEN-01", "Photos"), (response.StatusCode, problem.Text("code"), problem.Text("field")));
    }

    // NFR-14: the 3 files of 10 MB a log may hold (owner decision of 12/11/2026) must fit the limit with the other fields and the multipart envelope.
    [Fact]
    public async Task Three_photos_of_ten_megabytes_fit_the_request_limit()
    {
        var setup = await api.CycleAsync();
        using var client = await OnKestrelAsync(setup.Owner.Account.Username);
        var photo = Sized(Jpeg, (int)(10 * Megabyte));
        var form = new LogFields { Notes = new string('x', 2000), Photos = Enumerable.Repeat(photo, 3).ToList() }.ToForm();

        using var response = await client.PostAsync($"api/cultivation-cycles/{setup.CycleId}/cultivation-logs", form);

        var saved = (await response.ReadAsync(HttpStatusCode.Created)).GetProperty("log");
        Assert.Equal(Enumerable.Range(1, 3), saved.PhotoSlots());
    }

    /// <summary>Zeros written on demand: never built in memory, and never sent when the body is refused.</summary>
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
