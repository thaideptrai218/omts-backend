using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using OMTS.Api.IntegrationTests.Farming;
using OMTS.Api.IntegrationTests.Infrastructure;
using OMTS.Application.Features.Files;
using OMTS.Domain.Entities.Farming;
using OMTS.Domain.Entities.Harvest;
using static OMTS.Api.IntegrationTests.Sales.SalesInvoiceSendingTestKit;
using static OMTS.Api.IntegrationTests.Sales.SalesInvoiceTestKit;

namespace OMTS.Api.IntegrationTests.Traceability;

/// <summary>
/// BR-ORIGIN-01, BR-ORIGIN-04, NFR-03 acceptance: the data of the page, called directly without logging in, holds exactly the fields of BR-ORIGIN-03 and BR-FARM-09 (AC-11: no
/// customer, price, amount, debt or return), in Vietnamese or English (BR-ORIGIN-02).
/// </summary>
public class ProvenancePageWhitelistTests
{
    private static readonly SortedSet<string> Whitelist = ProvenancePageTestKit.Paths(
        "language", "status", "message", "messageCode", "messageParams",
        "invoice", "invoice.code", "invoice.date",
        "cooperative", "cooperative.fullName", "cooperative.shortName", "cooperative.yearEstablished", "cooperative.officeAddress", "cooperative.phoneNumber", "cooperative.email",
        "cooperative.website", "cooperative.socialLink", "cooperative.logoUrl", "cooperative.coverPhotoUrl", "cooperative.introduction", "cooperative.certifications",
        "cooperative.certifications[].name", "cooperative.certifications[].number", "cooperative.certifications[].issuer", "cooperative.certifications[].expiryDate",
        "cooperative.certifications[].state", "cooperative.certifications[].fileUrl",
        "lots", "lots[].lotCode", "lots[].products", "lots[].originState", "lots[].withdrawalDate", "lots[].message", "lots[].messageCode", "lots[].messageParams",
        "lots[].messageParams.cooperative_short_name", "lots[].messageParams.lot_code", "lots[].messageParams.withdrawal_date", "lots[].messageParams.cooperative_phone",
        "lots[].origin", "lots[].origin.harvestDate", "lots[].origin.raftCode", "lots[].origin.zoneName", "lots[].origin.map", "lots[].origin.map.latitude",
        "lots[].origin.map.longitude", "lots[].origin.map.source", "lots[].origin.seedings", "lots[].origin.seedings[].seedingDate", "lots[].origin.seedings[].seedItemName",
        "lots[].origin.daysOfFarming", "lots[].origin.harvestPhotoUrls", "lots[].origin.raftPhotoUrls");

    [Fact]
    public async Task The_page_data_holds_exactly_the_whitelisted_fields_and_no_hidden_value()
    {
        using var api = new AuthApiFactory();
        var sale = await api.SaleSetupAsync();
        var profile = await api.CooperativeProfileAsync();
        await api.SetReceivingAccountAsync();
        await api.UpdateAsync<Raft>(sale.RaftId, r => (r.Latitude, r.Longitude) = (21.0512m, 107.4321m));
        await api.UseDbAsync(db =>
        {
            db.HarvestLotPhotos.Add(new HarvestLotPhoto { LotId = sale.LotId, SortOrder = 1, FileKey = $"{FileCategories.HarvestLotPhotos}/2026/11/{Guid.NewGuid():N}.jpg" });
            db.RaftPhotos.Add(new RaftPhoto { RaftId = sale.RaftId, SortOrder = 1, FileKey = $"{FileCategories.RaftPhotos}/2026/11/{Guid.NewGuid():N}.jpg" });
        });
        var (invoice, token) = await sale.SoldWithQrCodeAsync((sale.LotId, 300m), (sale.OtherLotId, 200m));
        await api.UpdateAsync<HarvestLot>(sale.OtherLotId, l => (l.WithdrawnAt, l.WithdrawalReason) = (DateTimeOffset.UtcNow, "DEMO internal withdrawal reason"));
        var customer = await api.QueryAsync(db => db.Customers.AsNoTracking().SingleAsync(c => c.Id == sale.CustomerId));
        var supplierNames = await api.QueryAsync(db => db.Suppliers.Select(s => s.Name).ToListAsync());

        var response = await api.CreateClient().GetProvenanceAsync(token);
        var raw = await response.Content.ReadAsStringAsync();
        var page = JsonDocument.Parse(raw).RootElement;

        Assert.Equal(Whitelist, page.PropertyPaths());
        Assert.All(
            new[]
            {
                customer.Name, customer.PhoneNumber!, invoice.Id().ToString(), sale.LotId.ToString(), sale.OtherLotId.ToString(), sale.RaftId.ToString(), sale.ZoneId.ToString(),
                "DEMO internal withdrawal reason", profile.TaxCode, DemoAccountNumber, DemoBankName, profile.ExpiredName,
            }.Concat(supplierNames),
            hidden => Assert.DoesNotContain(hidden, raw, StringComparison.OrdinalIgnoreCase));

        var published = page.LotsByCode()[(await api.LotRowAsync(sale.LotId)).LotCode].GetProperty("origin");
        Assert.Equal(("Raft", 21.0512m, 107.4321m), (published.GetProperty("map").Text("source"), published.GetProperty("map").GetProperty("latitude").GetDecimal(),
            published.GetProperty("map").GetProperty("longitude").GetDecimal()));
        Assert.Equal((1, 1), (published.GetProperty("harvestPhotoUrls").GetArrayLength(), published.GetProperty("raftPhotoUrls").GetArrayLength()));
        Assert.Equal([profile.CertificationName], page.GetProperty("cooperative").GetProperty("certifications").EnumerateArray().Select(c => c.Text("name")));
    }

    // BR-ORIGIN-02: the page opens in Vietnamese; with lang=en the messages and the introduction are in English; names stay as entered.
    [Fact]
    public async Task The_page_is_in_vietnamese_by_default_and_in_english_on_request()
    {
        using var api = new AuthApiFactory();
        var sale = await api.SaleSetupAsync();
        var profile = await api.CooperativeProfileAsync();
        var (_, token) = await sale.SoldWithQrCodeAsync((sale.LotId, 10m), (sale.OtherLotId, 5m));
        await api.UpdateAsync<HarvestLot>(sale.OtherLotId, l => l.WithdrawnAt = DateTimeOffset.UtcNow);
        var lotCode = (await api.LotRowAsync(sale.OtherLotId)).LotCode;
        var client = api.CreateClient();

        var vi = await client.ProvenanceAsync(token);
        var viExplicit = await client.ProvenanceAsync(token, "vi");
        var en = await client.ProvenanceAsync(token, "en");

        Assert.Equal(("vi", "vi", "en"), (vi.Text("language"), viExplicit.Text("language"), en.Text("language")));
        Assert.Equal((profile.IntroductionVi, profile.IntroductionEn), (vi.GetProperty("cooperative").Text("introduction"), en.GetProperty("cooperative").Text("introduction")));
        Assert.StartsWith($"{profile.ShortName} đã thu hồi thông tin nguồn gốc của lô {lotCode} vào ngày ", vi.LotsByCode()[lotCode].Text("message"));
        Assert.StartsWith($"{profile.ShortName} withdrew the origin information of lot {lotCode} on ", en.LotsByCode()[lotCode].Text("message"));
        Assert.Equal(vi.LotsByCode()[lotCode].Text("messageCode"), en.LotsByCode()[lotCode].Text("messageCode"));
        Assert.Equal(profile.FullName, en.GetProperty("cooperative").Text("fullName"));
    }
}
