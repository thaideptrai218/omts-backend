using System.Net.Http.Headers;
using System.Net;
using System.Text.Json;
using OMTS.Api.IntegrationTests.Infrastructure;
using OMTS.Application.Features.Authorization;
using OMTS.Domain.Enums;
using static OMTS.Api.IntegrationTests.Infrastructure.AcsTestKit;

namespace OMTS.Api.IntegrationTests.Platform;

/// <summary>
/// QA of the OMTS-214 oversized upload answer on a real Kestrel host: every upload endpoint (file parameter,
/// file collection, file property of a form model), a chunked body, the same field as the endpoint's own file
/// rules, and the order against authentication, permissions and the password-change gate.
/// </summary>
public class OversizedUploadQaTests(KestrelApiFactory api) : IClassFixture<KestrelApiFactory>
{
    private const long Megabyte = 1024 * 1024;
    private const string Logo = "api/cooperative/profile/logo";

    // Sent with Expect: 100-continue, so the bodies (up to 106 MB) are never built in memory nor sent.
    [Theory]
    [InlineData("api/zones/{0}/water-measurements/with-photo", "photo", "Photo", 13)]
    [InlineData("api/rafts/{0}/photo", "file", "Photo", 12)]
    [InlineData("api/zone-licenses/{0}/replace", "file", "File", 12)]
    [InlineData("api/suppliers/{0}/certificates", "files", "Certificate", 106)]
    [InlineData("api/purchase-receipts/{0}/invoice-photos", "files", "Invoice photos", 106)]
    public async Task Every_upload_endpoint_answers_400_MSG01_on_its_file_field(string path, string multipartName, string field, int megabytes)
    {
        using var client = await OwnerOnKestrelAsync();

        using var response = await UploadAsync(client, string.Format(path, Guid.NewGuid()), multipartName, megabytes * Megabyte, expectContinue: true);

        var problem = await ProblemAsync(response);
        Assert.Equal((HttpStatusCode.BadRequest, "MSG-GEN-01", field), (response.StatusCode, Text(problem, "code"), Text(problem, "field")));
    }

    // A client streaming the file without a Content-Length (Transfer-Encoding: chunked) must get the same answer.
    [Fact]
    public async Task A_chunked_logo_above_the_limit_is_400_MSG01_on_the_file_field()
    {
        using var client = await OwnerOnKestrelAsync();

        using var response = await UploadAsync(client, Logo, "file", 4 * Megabyte, expectContinue: false, knownLength: false);

        var problem = await ProblemAsync(response);
        Assert.Equal((HttpStatusCode.BadRequest, "MSG-GEN-01", "Logo"), (response.StatusCode, Text(problem, "code"), Text(problem, "field")));
    }

    // A browser sends the whole body (no Expect: 100-continue). A 7 MB phone photo picked as the logo (limit 3 MB)
    // is discarded by Kestrel within the drain allowance, so the browser still reads the 400.
    [Fact]
    public async Task A_browser_upload_of_a_logo_over_twice_the_limit_still_reads_400_MSG01()
    {
        using var client = await OwnerOnKestrelAsync();

        using var response = await UploadAsync(client, Logo, "file", 7 * Megabyte, expectContinue: false);

        var problem = await ProblemAsync(response);
        Assert.Equal((HttpStatusCode.BadRequest, "MSG-GEN-01", "Logo"), (response.StatusCode, Text(problem, "code"), Text(problem, "field")));
    }

    // One mistake (the logo is too big), one answer: the 2.5 MB logo refused by the logo rules (2 MB) and the
    // 4 MB logo refused by the request limit (3 MB) must name the same field with the same MSG01 text.
    [Fact]
    public async Task A_logo_refused_by_the_request_limit_names_the_same_field_as_one_refused_by_the_logo_rules()
    {
        using var client = await OwnerOnKestrelAsync();

        using var byRules = await UploadAsync(client, Logo, "file", 5 * Megabyte / 2, expectContinue: false);
        using var byLimit = await UploadAsync(client, Logo, "file", 4 * Megabyte, expectContinue: true);

        var (rules, limit) = (await ProblemAsync(byRules), await ProblemAsync(byLimit));
        Assert.Equal((HttpStatusCode.BadRequest, HttpStatusCode.BadRequest), (byRules.StatusCode, byLimit.StatusCode));
        Assert.Equal((Text(rules, "code"), Text(rules, "field"), Text(rules, "detail")), (Text(limit, "code"), Text(limit, "field"), Text(limit, "detail")));
    }

    // Authentication, permissions and the account gate run before the resource filter: a caller who may not
    // upload at all never learns about the size.
    [Fact]
    public async Task Oversized_uploads_are_refused_by_authentication_permission_and_password_gate_first()
    {
        await api.EnsureCooperativeAsync();
        var staff = await api.AddAccountAsync(NewCode(), Password, SubjectType.Staff);
        await api.GrantAsync(staff, await api.AddRoleAsync($"Water {NewCode()}", (PermissionFunctions.WaterMeasurements, true, true, true)), null);
        var mustChange = await api.AddAccountAsync(NewCode(), Password, configure: a => { a.MustChangePassword = true; a.TemporaryPasswordIssuedAt = DateTimeOffset.UtcNow; });
        using var anonymous = KestrelClient();
        using var staffClient = await OnKestrelAsync(staff.Username);
        using var mustChangeClient = await OnKestrelAsync(mustChange.Username);

        using var unauthenticated = await UploadAsync(anonymous, Logo, "file", 4 * Megabyte, expectContinue: true);
        using var forbidden = await UploadAsync(staffClient, Logo, "file", 4 * Megabyte, expectContinue: true);
        using var gated = await UploadAsync(mustChangeClient, Logo, "file", 4 * Megabyte, expectContinue: true);

        Assert.Equal(HttpStatusCode.Unauthorized, unauthenticated.StatusCode);
        Assert.Equal((HttpStatusCode.Forbidden, "MSG-GEN-08"), (forbidden.StatusCode, Text(await ProblemAsync(forbidden), "code")));
        Assert.Equal((HttpStatusCode.Forbidden, "PASSWORD_CHANGE_REQUIRED"), (gated.StatusCode, Text(await ProblemAsync(gated), "code")));
    }

    private async Task<HttpClient> OwnerOnKestrelAsync()
    {
        await api.EnsureCooperativeAsync();
        return await OnKestrelAsync((await api.AddAccountAsync(NewCode(), Password)).Username);
    }

    // HttpClient sends the body anyway when no 100 Continue arrives within Expect100ContinueTimeout (1 s by
    // default), and Kestrel resets a connection whose unrequested body keeps coming: under a loaded test run
    // that turns into a connection reset, so the client waits for the server's answer instead.
    private HttpClient KestrelClient()
    {
        using var probe = api.CreateKestrelClient();
        return new HttpClient(new SocketsHttpHandler { Expect100ContinueTimeout = TimeSpan.FromMinutes(2) }) { BaseAddress = probe.BaseAddress };
    }

    private async Task<HttpClient> OnKestrelAsync(string username)
    {
        var client = KestrelClient();
        var tokens = await LoginTokensAsync(client, username);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", tokens.GetProperty("accessToken").GetString());
        return client;
    }

    private static Task<HttpResponseMessage> UploadAsync(
        HttpClient client, string path, string field, long bytes, bool expectContinue, bool knownLength = true)
    {
        var content = new MultipartFormDataContent { { new ZeroContent(bytes, knownLength), field, "upload.png" } };
        var request = new HttpRequestMessage(HttpMethod.Post, path) { Content = content };
        request.Headers.ExpectContinue = expectContinue;
        return client.SendAsync(request);
    }

    private static async Task<JsonElement> ProblemAsync(HttpResponseMessage response)
    {
        var text = await response.Content.ReadAsStringAsync();
        try
        {
            return JsonDocument.Parse(text).RootElement.Clone();
        }
        catch (JsonException)
        {
            Assert.Fail($"HTTP {(int)response.StatusCode} without a problem body: {text}");
            throw;
        }
    }

    private static string? Text(JsonElement problem, string property) =>
        problem.TryGetProperty(property, out var value) ? value.ToString() : $"(no {property})";

    /// <summary>Zeros written on demand: a large body costs no memory, and nothing at all when it is never sent.</summary>
    private sealed class ZeroContent(long length, bool knownLength) : HttpContent
    {
        protected override async Task SerializeToStreamAsync(Stream stream, TransportContext? context)
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
            return knownLength;
        }
    }
}
