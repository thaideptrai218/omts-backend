using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using OMTS.Api.IntegrationTests.Cultivation;
using OMTS.Api.IntegrationTests.Harvest;
using OMTS.Api.IntegrationTests.Infrastructure;
using OMTS.Application.Features.Sales.QualityClaims;
using OMTS.Domain.Enums;
using static OMTS.Api.IntegrationTests.Farming.FarmingTestKit;
using static OMTS.Api.IntegrationTests.Sales.SalesInvoiceTestKit;

namespace OMTS.Api.IntegrationTests.Sales;

/// <summary>A trader with an invoice of 10 kg at the list price (300,000 VND), the Owner and the Zone Manager of its zone.</summary>
public sealed record ClaimWorld(SaleSetup Sale, TraderActor Trader, Guid InvoiceId);

public sealed class StubSalesReturnCheck : IInvoiceSalesReturnCheck
{
    public HashSet<Guid> Returned { get; } = [];

    public Task<bool> HasSalesReturnAsync(Guid invoiceId, CancellationToken cancellationToken) => Task.FromResult(Returned.Contains(invoiceId));
}

/// <summary>The host with the sales return check stubbed, so a test can mark an invoice returned without recording a return.</summary>
public sealed class QualityClaimApiFactory : AuthApiFactory
{
    public StubSalesReturnCheck Returns { get; } = new();

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);
        builder.ConfigureTestServices(services => services.AddSingleton<IInvoiceSalesReturnCheck>(Returns));
    }
}

/// <summary>CLAIM-01 and CLAIM-02 calls: the multipart body as the client sends it.</summary>
public static class QualityClaimTestKit
{
    public const string Description = "Oysters arrived dead, box 3";

    public static async Task<ClaimWorld> ClaimWorldAsync(this AuthApiFactory api)
    {
        var sale = await api.SaleSetupAsync();
        var trader = await api.TraderAsync();
        var invoice = await sale.SellToAsync(trader.Profile.Id, sale.LotId);
        return new ClaimWorld(sale, trader, invoice);
    }

    /// <summary>An invoice of 10 kg, the lot staying for sale, so a test can sell from it again.</summary>
    public static async Task<Guid> SellToAsync(this SaleSetup sale, Guid? customerId, Guid lotId) =>
        (await sale.Owner.Client.SellAsync(Invoice(customerId, [Line(sale.ProductId, lotId, 10m)], Cash(300_000m), lotId))).GetProperty("invoice").Id();

    public static Task<HttpResponseMessage> PostClaimAsync(
        this HttpClient trader, Guid invoiceId, string? description = Description, IReadOnlyList<byte[]>? photos = null, string photoField = "photos")
    {
        var form = new MultipartFormDataContent();
        if (description is not null)
        {
            form.Add(new StringContent(description), "description");
        }

        foreach (var photo in photos ?? [Jpeg])
        {
            form.Add(new ByteArrayContent(photo), photoField, "photo.jpg");
        }

        return trader.PostAsync($"/api/me/sales-invoices/{invoiceId}/quality-claims", form);
    }

    /// <summary>The claim the trader sent.</summary>
    public static async Task<JsonElement> SubmitClaimAsync(this HttpClient trader, Guid invoiceId, IReadOnlyList<byte[]>? photos = null) =>
        (await (await trader.PostClaimAsync(invoiceId, photos: photos)).ReadAsync(HttpStatusCode.Created)).GetProperty("claim");

    public static async Task<JsonElement> ClaimAsStaffAsync(this HttpClient staff, Guid claimId) =>
        await (await staff.GetAsync($"/api/quality-claims/{claimId}")).ReadAsync(HttpStatusCode.OK);

    /// <summary>The Owner proposes an offer; a price cut offer carries its own amount (30,000 unless given).</summary>
    public const decimal DefaultCutAmount = 30_000m;

    public static Task<HttpResponseMessage> AcceptClaimAsync(
        this HttpClient owner, Guid claimId, string? rowVersion, string offer = "ReturnAllGoods", string? note = "The cooperative is sorry", decimal? priceCutAmount = null) =>
        owner.PostAsJsonAsync($"/api/quality-claims/{claimId}/accept", new { offer, note, rowVersion, priceCutAmount = priceCutAmount ?? (offer == "PriceCutWholeInvoice" ? DefaultCutAmount : (decimal?)null) });

    public static Task<HttpResponseMessage> AcceptOfferAsync(this HttpClient trader, Guid claimId, string? note = null) =>
        trader.PostAsJsonAsync($"/api/me/quality-claims/{claimId}/accept-offer", new { note });

    public static Task<HttpResponseMessage> DeclineOfferAsync(this HttpClient trader, Guid claimId, string? note = null) =>
        trader.PostAsJsonAsync($"/api/me/quality-claims/{claimId}/decline-offer", new { note });

    public static Task<HttpResponseMessage> RejectClaimAsync(this HttpClient owner, Guid claimId, string? rowVersion, string? note = "Not a quality problem") =>
        owner.PostAsJsonAsync($"/api/quality-claims/{claimId}/reject", new { note, rowVersion });

    public static Task<JsonElement> ProblemAsync(this HttpResponseMessage response, HttpStatusCode status, string code) => Purchasing.PurchasingTestKit.ProblemAsync(response, status, code);

    public static QualityClaimOffer? ReadOffer(this JsonElement claim) =>
        claim.GetProperty("offer").ValueKind == JsonValueKind.Null ? null : claim.GetProperty("offer").Deserialize<QualityClaimOffer>(new JsonSerializerOptions { Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() } });
}
