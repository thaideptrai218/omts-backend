using System.Net;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using OMTS.Api.IntegrationTests.Farming;
using OMTS.Api.IntegrationTests.Infrastructure;
using OMTS.Domain.Entities.Identity;
using SaleSetup = OMTS.Api.IntegrationTests.Sales.SaleSetup;
using static OMTS.Api.IntegrationTests.Sales.SalesInvoiceSendingTestKit;
using static OMTS.Api.IntegrationTests.Sales.SalesInvoiceTestKit;

namespace OMTS.Api.IntegrationTests.Traceability;

/// <summary>The cooperative block the tests give the cooperative: what the page must show, and what it must never show.</summary>
public sealed record OriginProfile(string FullName, string ShortName, string Phone, string TaxCode, string IntroductionVi, string IntroductionEn, string CertificationName, string ExpiredName);

/// <summary>ORIGIN-01 calls: the public page of a QR code, the sale that gives an invoice its QR code, and the JSON shape of a page.</summary>
public static class ProvenancePageTestKit
{
    public static Task<HttpResponseMessage> GetProvenanceAsync(this HttpClient client, string qrToken, string? lang = null) =>
        client.GetAsync($"/api/public/provenance/{Uri.EscapeDataString(qrToken)}{(lang is null ? string.Empty : $"?lang={lang}")}");

    public static async Task<JsonElement> ProvenanceAsync(this HttpClient client, string qrToken, string? lang = null) =>
        await (await client.GetProvenanceAsync(qrToken, lang)).ReadAsync(HttpStatusCode.OK);

    /// <summary>
    /// The Owner sells these kg of these lots at the list price, paid in cash and not as whole lots, and ends the sale with a Yes to the QR question: the ready lots are published
    /// (TRACE-01) and the invoice gets its QR code. Answers the invoice and its token.
    /// </summary>
    public static async Task<(JsonElement Invoice, string Token)> SoldWithQrCodeAsync(this SaleSetup sale, params (Guid LotId, decimal Kg)[] lines)
    {
        var body = Invoice(
            sale.CustomerId, lines.Select(l => Line(sale.ProductId, l.LotId, l.Kg)).ToArray(), Cash(lines.Sum(l => l.Kg) * ListPrice), lines.Select(l => l.LotId).Distinct().ToArray());
        var invoice = (await sale.Owner.Client.SellAsync(body)).GetProperty("invoice");
        var finished = await sale.Owner.Client.FinishedAsync(invoice.Id(), wantsQrCode: true);
        return (invoice, finished.Text("qrUrl").Split('/')[^1]);
    }

    /// <summary>A full cooperative profile with a valid and an Expired certification (BR-FARM-09).</summary>
    public static async Task<OriginProfile> CooperativeProfileAsync(this AuthApiFactory api)
    {
        var profile = new OriginProfile(
            "DEMO Oyster Cooperative", "DEMO HTX", AcsTestKit.NewPhone(), "9999000001", "Hợp tác xã nuôi hàu DEMO.", "DEMO oyster cooperative.", "DEMO VietGAP", "DEMO expired label");
        var id = await api.QueryAsync(db => db.Cooperatives.Select(c => c.Id).FirstAsync());
        await api.UpdateAsync<Cooperative>(id, c =>
        {
            (c.FullName, c.ShortName, c.PhoneNumber, c.TaxCode) = (profile.FullName, profile.ShortName, profile.Phone, profile.TaxCode);
            (c.IntroductionVi, c.IntroductionEn, c.YearEstablished, c.OfficeAddress) = (profile.IntroductionVi, profile.IntroductionEn, 2015, "DEMO address");
        });
        await api.UseDbAsync(db => db.CooperativeCertifications.AddRange(
            new CooperativeCertification { CooperativeId = id, Name = profile.CertificationName, Number = "DEMO-01", Issuer = "DEMO issuer", ExpiryDate = FarmingTestKit.Today.AddYears(1) },
            new CooperativeCertification { CooperativeId = id, Name = profile.ExpiredName, ExpiryDate = FarmingTestKit.Today.AddDays(-1) }));
        return profile;
    }

    /// <summary>The listed lots of a page by lot code.</summary>
    public static Dictionary<string, JsonElement> LotsByCode(this JsonElement page) => page.GetProperty("lots").EnumerateArray().ToDictionary(l => l.Text("lotCode"));

    /// <summary>A set of property paths in the order of <see cref="PropertyPaths"/>, to compare with it.</summary>
    public static SortedSet<string> Paths(params string[] paths) => new(paths, StringComparer.Ordinal);

    /// <summary>Every property path of the JSON, e.g. "lots[].origin.map.source": what the page data holds (BR-ORIGIN-01, NFR-03).</summary>
    public static SortedSet<string> PropertyPaths(this JsonElement element)
    {
        var paths = new SortedSet<string>(StringComparer.Ordinal);
        Walk(element, string.Empty);
        return paths;

        void Walk(JsonElement e, string prefix)
        {
            if (e.ValueKind == JsonValueKind.Object)
            {
                foreach (var property in e.EnumerateObject())
                {
                    var path = prefix.Length == 0 ? property.Name : $"{prefix}.{property.Name}";
                    paths.Add(path);
                    Walk(property.Value, path);
                }
            }
            else if (e.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in e.EnumerateArray())
                {
                    Walk(item, prefix + "[]");
                }
            }
        }
    }
}
