using System.Net;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using OMTS.Api.IntegrationTests.Farming;
using OMTS.Api.IntegrationTests.Infrastructure;
using static OMTS.Api.IntegrationTests.Sales.SalesInvoiceTestKit;

namespace OMTS.Api.IntegrationTests.Traceability;

/// <summary>ORIGIN-01 access: anonymous and logged-in viewers see the same page (BR-ORIGIN-01), an unknown QR code (ORIGIN-01.0.E1), a cancelled invoice (ORIGIN-01.0.E2), the limit.</summary>
public class ProvenancePageAccessTests
{
    private const string UnknownVi = "Không tìm thấy hóa đơn của mã QR này. Vui lòng quét lại mã QR trên hóa đơn, hoặc mở đường link trong tin nhắn hóa đơn.";
    private const string UnknownEn = "No invoice was found for this QR code. Please scan the QR code on the invoice again, or open the link in the invoice message.";

    // BR-ORIGIN-01, §I.4.4 (Full for every role): no login is needed, a logged-in user sees the same page, and the page is never cached (BR-TRACE-05).
    [Fact]
    public async Task Anyone_opens_the_page_without_logging_in_and_a_logged_in_user_sees_the_same()
    {
        using var api = new AuthApiFactory();
        var sale = await api.SaleSetupAsync();
        var (_, token) = await sale.SoldWithQrCodeAsync((sale.LotId, 10m));

        var anonymous = await api.CreateClient().GetProvenanceAsync(token);
        var asOwner = await sale.Owner.Client.GetProvenanceAsync(token);
        var asManager = await sale.Manager.Client.GetProvenanceAsync(token);

        Assert.Equal(HttpStatusCode.OK, anonymous.StatusCode);
        var page = await anonymous.Content.ReadAsStringAsync();
        Assert.Equal(page, await asOwner.Content.ReadAsStringAsync());
        Assert.Equal(page, await asManager.Content.ReadAsStringAsync());
        Assert.True(anonymous.Headers.CacheControl?.NoStore, anonymous.Headers.CacheControl?.ToString());
    }

    // ORIGIN-01.0.E1: a token no invoice has, a link that is no QR code at all and a token of another case are answered alike (404 MSG-ORIGIN-03), so nothing tells them apart.
    [Fact]
    public async Task An_unknown_or_malformed_qr_code_is_not_found_in_the_page_language()
    {
        using var api = new AuthApiFactory();
        var sale = await api.SaleSetupAsync();
        var (_, token) = await sale.SoldWithQrCodeAsync((sale.LotId, 10m));
        var client = api.CreateClient();
        var neverGiven = Convert.ToBase64String(RandomNumberGenerator.GetBytes(16)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        var otherCase = token.Any(char.IsLetter) ? new string(token.Select(c => char.IsUpper(c) ? char.ToLowerInvariant(c) : char.ToUpperInvariant(c)).ToArray()) : null;

        foreach (var link in new[] { neverGiven, "not-a-qr-code", token + "A", "' or 1=1 --", otherCase }.OfType<string>())
        {
            var vi = await (await client.GetProvenanceAsync(link)).SrsProblemAsync(HttpStatusCode.NotFound, "MSG-ORIGIN-03", null);
            Assert.Equal(UnknownVi, vi.Detail());
            var en = await (await client.GetProvenanceAsync(link, "en")).SrsProblemAsync(HttpStatusCode.NotFound, "MSG-ORIGIN-03", null);
            Assert.Equal(UnknownEn, en.Detail());
        }
    }

    // ORIGIN-01.0.E2, BR-ORIGIN-05: a cancelled invoice keeps its QR code and shows the cooperative block with MSG-ORIGIN-04 only, without its code or its lots.
    [Fact]
    public async Task A_cancelled_invoice_shows_the_cooperative_block_and_the_note_only()
    {
        using var api = new AuthApiFactory();
        var sale = await api.SaleSetupAsync();
        var profile = await api.CooperativeProfileAsync();
        var (invoice, token) = await sale.SoldWithQrCodeAsync((sale.LotId, 10m));
        await api.CancelInvoiceAsync(invoice.Id());

        var page = await api.CreateClient().ProvenanceAsync(token, "en");

        Assert.Equal(("en", "Cancelled", "MSG-ORIGIN-04"), (page.Text("language"), page.Text("status"), page.Text("messageCode")));
        Assert.Equal($"This invoice has been cancelled, so no origin information is shown. For details, please call {profile.Phone}.", page.Text("message"));
        Assert.Equal(profile.Phone, page.GetProperty("messageParams").Text("cooperative_phone"));
        Assert.Equal(JsonValueKind.Null, page.GetProperty("invoice").ValueKind);
        Assert.Equal(0, page.GetProperty("lots").GetArrayLength());
        Assert.Equal((profile.FullName, profile.IntroductionEn), (page.GetProperty("cooperative").Text("fullName"), page.GetProperty("cooperative").Text("introduction")));
        Assert.Equal(ProvenancePageTestKit.Paths(
            "cooperative", "cooperative.certifications", "cooperative.certifications[].expiryDate", "cooperative.certifications[].fileUrl", "cooperative.certifications[].issuer",
                "cooperative.certifications[].name", "cooperative.certifications[].number", "cooperative.certifications[].state", "cooperative.coverPhotoUrl", "cooperative.email",
                "cooperative.fullName", "cooperative.introduction", "cooperative.logoUrl", "cooperative.officeAddress", "cooperative.phoneNumber", "cooperative.shortName",
                "cooperative.socialLink", "cooperative.website", "cooperative.yearEstablished", "invoice", "language", "lots", "message", "messageCode", "messageParams",
                "messageParams.cooperative_phone", "status"),
            page.PropertyPaths());
    }

    // The page is limited per client IP like the other anonymous endpoints: over the limit, 429 TOO_MANY_REQUESTS with Retry-After, before any lookup.
    [Fact]
    public async Task Pages_over_the_per_minute_limit_get_429()
    {
        using var factory = new AuthApiFactory();
        using var api = factory.WithWebHostBuilder(b => b.UseSetting("RateLimits:ProvenancePagesPerMinute", "2"));
        var client = api.CreateClient();

        var answers = await Task.WhenAll(Enumerable.Range(0, 3).Select(_ => client.GetProvenanceAsync("AAAAAAAAAAAAAAAAAAAAAA")));

        Assert.Equal(2, answers.Count(r => r.StatusCode == HttpStatusCode.NotFound));
        var refused = Assert.Single(answers, r => r.StatusCode == HttpStatusCode.TooManyRequests);
        Assert.Equal("TOO_MANY_REQUESTS", await refused.ErrorCodeAsync());
        Assert.Equal("60", refused.Headers.RetryAfter?.ToString());
    }
}
