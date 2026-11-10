using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using OMTS.Api.IntegrationTests.Infrastructure;
using OMTS.Api.IntegrationTests.Purchasing;
using OMTS.Domain.Constants;
using static OMTS.Api.IntegrationTests.Infrastructure.AcsTestKit;
using static OMTS.Api.IntegrationTests.Sales.SalesCatalogueTestKit;
using static OMTS.Api.IntegrationTests.Sales.SalesInvoiceTestKit;

namespace OMTS.Api.IntegrationTests.Sales;

/// <summary>QA of the claims on a real Kestrel host (the body limit) and on Azurite (the private container and the SAS link).</summary>
public class QualityClaimQaHostTests
{
    private const long Megabyte = 1024 * 1024;

    private sealed class ZeroContent(long length) : HttpContent
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
            return true;
        }
    }

    private sealed class BlobApiFactory(string connectionString, string container) : AuthApiFactory
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            base.ConfigureWebHost(builder);
            builder.UseSetting("Storage:Provider", "AzureBlob");
            builder.UseSetting("Storage:AzureBlob:ConnectionString", connectionString);
            builder.UseSetting("Storage:AzureBlob:Container", container);
        }
    }

    [Fact]
    public async Task A_body_above_the_upload_limit_answers_msg_gen_01_on_the_photos_on_real_kestrel()
    {
        using var api = new KestrelApiFactory();
        await api.EnsureCooperativeAsync();
        var (account, _) = await api.LinkedProfileAsync(null, null);
        using var probe = api.CreateKestrelClient();
        using var client = new HttpClient(new SocketsHttpHandler { Expect100ContinueTimeout = TimeSpan.FromMinutes(2) }) { BaseAddress = probe.BaseAddress };
        var tokens = await LoginTokensAsync(client, account.Username);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", tokens.GetProperty("accessToken").GetString());

        var content = new MultipartFormDataContent { { new ZeroContent(106 * Megabyte), "photos", "upload.jpg" } };
        var request = new HttpRequestMessage(HttpMethod.Post, $"api/me/sales-invoices/{Guid.NewGuid()}/quality-claims") { Content = content };
        request.Headers.ExpectContinue = true;

        using var response = await client.SendAsync(request);

        var body = await response.ReadAsync(HttpStatusCode.BadRequest);
        Assert.Equal(("MSG-GEN-01", "Photos"), (body.Text("code"), body.Text("field")));
    }

    [AzuriteFact]
    public async Task On_azure_blob_the_photo_is_private_its_sas_link_lasts_an_hour_and_a_changed_signature_opens_nothing()
    {
        using var api = new BlobApiFactory(Environment.GetEnvironmentVariable(AzuriteFactAttribute.VariableName)!, $"omts-test-{Guid.NewGuid():N}");
        var world = await api.ClaimWorldAsync();

        var claim = await world.Trader.Client.SubmitClaimAsync(world.InvoiceId);
        var url = claim.GetProperty("photos")[0].Text("url")!;
        using var http = new HttpClient();

        var withLink = await http.GetAsync(url);
        Assert.Equal(HttpStatusCode.OK, withLink.StatusCode);
        Assert.Equal("image/jpeg", withLink.Content.Headers.ContentType?.MediaType);

        var query = System.Web.HttpUtility.ParseQueryString(new Uri(url).Query);
        var expires = DateTimeOffset.Parse(query["se"]!, System.Globalization.CultureInfo.InvariantCulture);
        Assert.InRange(expires, DateTimeOffset.UtcNow.AddMinutes(59), DateTimeOffset.UtcNow.AddMinutes(61));
        Assert.Equal("r", query["sp"]);

        var noSignature = new UriBuilder(url) { Query = string.Empty }.Uri;
        Assert.NotEqual(HttpStatusCode.OK, (await http.GetAsync(noSignature)).StatusCode);

        var tampered = url.Replace(Uri.EscapeDataString(query["sig"]!), "AAAA", StringComparison.Ordinal);
        Assert.NotEqual(url, tampered);
        Assert.NotEqual(HttpStatusCode.OK, (await http.GetAsync(tampered)).StatusCode);
    }

    [Fact]
    public async Task Accepting_or_rejecting_a_claim_answers_msg_gen_05()
    {
        using var api = new QualityClaimApiFactory();
        var accept = await api.QaClaimedAsync();
        var reject = await api.QaClaimedAsync();

        var accepted = await (await accept.Owner.AcceptClaimAsync(accept.Id, accept.RowVersion)).ReadAsync(HttpStatusCode.OK);
        var rejected = await (await reject.Owner.RejectClaimAsync(reject.Id, reject.RowVersion)).ReadAsync(HttpStatusCode.OK);

        // CLAIM-02 step 5: "records it in the audit log and shows that it is saved (MSG-GEN-05)"; CLAIM-01 step 5 sends it.
        Assert.True(accepted.TryGetProperty("messageCode", out var a) && a.ValueKind == JsonValueKind.String, "The accept answer holds no message.");
        Assert.Equal("MSG-GEN-05", a.GetString());
        Assert.Equal("MSG-GEN-05", rejected.GetProperty("messageCode").GetString());
    }
}
