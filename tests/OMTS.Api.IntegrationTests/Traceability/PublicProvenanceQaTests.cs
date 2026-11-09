using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using OMTS.Api.IntegrationTests.Cultivation;
using OMTS.Api.IntegrationTests.Farming;
using OMTS.Api.IntegrationTests.Harvest;
using OMTS.Api.IntegrationTests.Infrastructure;
using OMTS.Application.Common;
using OMTS.Application.Features.Files;
using OMTS.Domain.Entities.Cultivation;
using OMTS.Domain.Entities.Farming;
using OMTS.Domain.Entities.Harvest;
using OMTS.Domain.Entities.Identity;
using static OMTS.Api.IntegrationTests.Farming.FarmingTestKit;
using static OMTS.Api.IntegrationTests.Sales.SalesInvoiceSendingTestKit;
using static OMTS.Api.IntegrationTests.Sales.SalesInvoiceTestKit;
using Catalogue = OMTS.Api.IntegrationTests.Sales.SalesCatalogueTestKit;

namespace OMTS.Api.IntegrationTests.Traceability;

/// <summary>
/// QA of ORIGIN-01 (OMTS-222): tries to break the public provenance page on what the developer's tests do not cover — a lot of another zone, a lot on two invoices, a lot
/// published later, hidden values (BR-ORIGIN-04), the cooperative block (BR-FARM-09), photo links, a garbage login, caching and the per-IP limit.
/// </summary>
public class PublicProvenanceQaTests
{
    // BR-ORIGIN-03: the raft and zone of each lot are its own, also when the invoice is made in another zone (SALE-05: the invoice zone holds lines of lots of other zones),
    // and the pin is the zone's when the raft has no map location.
    [Fact]
    public async Task Qa_a_lot_of_another_zone_shows_its_own_raft_and_zone_and_the_zone_pin_when_its_raft_has_none()
    {
        using var api = new AuthApiFactory();
        var sale = await api.SaleSetupAsync();
        var owner = sale.Owner.Client;
        var otherZone = await owner.CreateZoneAsync(NewZoneBody() with { Latitude = 20.9123m, Longitude = 107.1234m });
        var otherRaft = await owner.CreateRaftAsync(otherZone.Id());
        await api.UpdateAsync<Raft>(otherRaft.Id(), r => (r.Latitude, r.Longitude) = (null, null));
        var cycle = await owner.StartCycleAsync(otherRaft.Id(), new CycleBody(Today.AddDays(-10)));
        var seed = await owner.SeedLotAsync();
        await owner.SeedingRecordAsync(cycle.Id(), seed.Session([1, 2, 3, 4, 5]));
        var otherLot = await owner.HarvestLotAsync(otherRaft.Id(), HarvestFields.OfLanes(1));

        var (_, token) = await sale.SoldWithQrCodeAsync((sale.LotId, 10m), (otherLot.Id(), 5m));

        var lots = (await api.CreateClient().ProvenanceAsync(token)).LotsByCode();
        var origin = lots[otherLot.Text("lotCode")].GetProperty("origin");
        Assert.Equal((otherRaft.Text("code"), otherZone.Text("name")), (origin.Text("raftCode"), origin.Text("zoneName")));
        var map = origin.GetProperty("map");
        Assert.Equal(("Zone", 20.9123m, 107.1234m), (map.Text("source"), map.GetProperty("latitude").GetDecimal(), map.GetProperty("longitude").GetDecimal()));
        var firstZoneName = await api.QueryAsync(db => db.FarmingZones.Where(z => z.Id == sale.ZoneId).Select(z => z.Name).SingleAsync());
        Assert.Equal(firstZoneName, lots[(await api.LotRowAsync(sale.LotId)).LotCode].GetProperty("origin").Text("zoneName"));
    }

    // BR-ORIGIN-05: each page shows what its own invoice sold (the products of that invoice only); a withdrawal shows on every page of the lot (BR-TRACE-06); cancelling one
    // invoice changes its page only ("the pages of the other invoices that sell the lot do not change").
    [Fact]
    public async Task Qa_a_lot_on_two_invoices_shows_each_invoices_products_and_a_cancel_changes_one_page_only()
    {
        using var api = new AuthApiFactory();
        var sale = await api.SaleSetupAsync();
        var owner = sale.Owner.Client;
        var secondProduct = await Catalogue.CreateProductAsync(owner, unitPrice: ListPrice);
        var (first, firstToken) = await sale.SoldWithQrCodeAsync((sale.LotId, 10m));
        var second = (await owner.SellAsync(Invoice(sale.CustomerId, [Line(secondProduct.Id(), sale.LotId, 4m)], Cash(4m * ListPrice), sale.LotId))).GetProperty("invoice");
        var secondToken = (await owner.FinishedAsync(second.Id(), wantsQrCode: true)).Text("qrUrl").Split('/')[^1];
        var lotCode = (await api.LotRowAsync(sale.LotId)).LotCode;
        var firstProduct = await api.QueryAsync(db => db.Products.Where(p => p.Id == sale.ProductId).Select(p => p.Name).SingleAsync());
        var client = api.CreateClient();

        Assert.NotEqual(firstToken, secondToken);
        Assert.Equal([firstProduct], (await client.ProvenanceAsync(firstToken)).LotsByCode()[lotCode].GetProperty("products").EnumerateArray().Select(p => p.GetString()));
        Assert.Equal([secondProduct.Text("name")], (await client.ProvenanceAsync(secondToken)).LotsByCode()[lotCode].GetProperty("products").EnumerateArray().Select(p => p.GetString()));

        await api.UpdateAsync<HarvestLot>(sale.LotId, l => l.WithdrawnAt = DateTimeOffset.UtcNow);
        foreach (var token in new[] { firstToken, secondToken })
        {
            Assert.Equal("Withdrawn", (await client.ProvenanceAsync(token)).LotsByCode()[lotCode].Text("originState"));
        }

        await api.CancelInvoiceAsync(first.Id());
        Assert.Equal("Cancelled", (await client.ProvenanceAsync(firstToken)).Text("status"));
        var other = await client.ProvenanceAsync(secondToken);
        Assert.Equal(("Ok", second.Text("code")), (other.Text("status"), other.GetProperty("invoice").Text("code")));
        Assert.Equal("Withdrawn", other.LotsByCode()[lotCode].Text("originState"));
    }

    // BR-TRACE-02 / BR-ORIGIN-05: a lot Not published at the end of the sale and published later (Trace lot screen) is listed at once, in line order; with no seeding session
    // found the days of farming are unknown, never made up (BR-TRACE-03).
    [Fact]
    public async Task Qa_a_lot_published_after_the_sale_is_listed_at_once_in_line_order()
    {
        using var api = new AuthApiFactory();
        var sale = await api.SaleSetupAsync();
        await api.LeaveLaneWithoutSeedLotAsync(sale.RaftId, 2);
        var (_, token) = await sale.SoldWithQrCodeAsync((sale.OtherLotId, 5m), (sale.LotId, 10m));
        var (lotCode, otherCode) = ((await api.LotRowAsync(sale.LotId)).LotCode, (await api.LotRowAsync(sale.OtherLotId)).LotCode);
        var client = api.CreateClient();

        Assert.Equal([lotCode], (await client.ProvenanceAsync(token)).LotsByCode().Keys);

        await api.UpdateAsync<HarvestLot>(sale.OtherLotId, l => l.PublishedAt = DateTimeOffset.UtcNow);

        var lots = (await client.ProvenanceAsync(token)).GetProperty("lots").EnumerateArray().ToList();
        Assert.Equal([otherCode, lotCode], lots.Select(l => l.Text("lotCode")));
        var origin = lots[0].GetProperty("origin");
        Assert.Equal((0, JsonValueKind.Null), (origin.GetProperty("seedings").GetArrayLength(), origin.GetProperty("daysOfFarming").ValueKind));
    }

    // BR-ORIGIN-01, NFR-03: the NoLots page (ORIGIN-01.2) also holds nothing but its whitelist: no lots, no origin, the phone parameter only.
    [Fact]
    public async Task Qa_the_no_lots_page_holds_exactly_its_whitelisted_fields()
    {
        using var api = new AuthApiFactory();
        var sale = await api.SaleSetupAsync();
        var (_, token) = await sale.SoldWithQrCodeAsync((sale.LotId, 10m));
        await api.UpdateAsync<HarvestLot>(sale.LotId, l => l.PublishedAt = null);

        var page = await api.CreateClient().ProvenanceAsync(token);

        Assert.Equal(("NoLots", "MSG-ORIGIN-02"), (page.Text("status"), page.Text("messageCode")));
        Assert.Equal(ProvenancePageTestKit.Paths(
                "language", "status", "message", "messageCode", "messageParams", "messageParams.cooperative_phone", "invoice", "invoice.code", "invoice.date", "lots",
                "cooperative", "cooperative.fullName", "cooperative.shortName", "cooperative.yearEstablished", "cooperative.officeAddress", "cooperative.phoneNumber",
                "cooperative.email", "cooperative.website", "cooperative.socialLink", "cooperative.logoUrl", "cooperative.coverPhotoUrl", "cooperative.introduction",
                "cooperative.certifications"),
            page.PropertyPaths());
    }

    // BR-ORIGIN-04, BR-FARM-09, NFR-03: whatever the page loads never holds the seed lot or receipt codes, the staff who recorded the data, the cultivation log photos, the
    // photos of a Withdrawn lot (its origin information), internal ids or the product and customer ids.
    [Fact]
    public async Task Qa_internal_values_and_non_public_photos_never_reach_the_page_data()
    {
        using var api = new AuthApiFactory();
        var sale = await api.SaleSetupAsync();
        var cycleId = await api.QueryAsync(db => db.HarvestLots.Where(l => l.Id == sale.LotId).Select(l => l.CycleId).SingleAsync());
        var logPhoto = $"{FileCategories.CultivationLogPhotos}/2026/11/{Guid.NewGuid():N}.jpg";
        var withdrawnPhoto = $"{FileCategories.HarvestLotPhotos}/2026/11/{Guid.NewGuid():N}.jpg";
        var publishedPhoto = $"{FileCategories.HarvestLotPhotos}/2026/11/{Guid.NewGuid():N}.jpg";
        await api.UseDbAsync(db =>
        {
            db.CultivationLogs.Add(new CultivationLog
            {
                CycleId = cycleId, VisitDate = Today, Notes = "DEMO internal log note", RecordedByAccountId = sale.Owner.Account.Id,
                Photos = [new CultivationLogPhoto { SortOrder = 1, FileKey = logPhoto }],
            });
            db.HarvestLotPhotos.Add(new HarvestLotPhoto { LotId = sale.OtherLotId, SortOrder = 1, FileKey = withdrawnPhoto });
            db.HarvestLotPhotos.Add(new HarvestLotPhoto { LotId = sale.LotId, SortOrder = 1, FileKey = publishedPhoto });
        });
        var (invoice, token) = await sale.SoldWithQrCodeAsync((sale.LotId, 10m), (sale.OtherLotId, 5m));
        await api.UpdateAsync<HarvestLot>(sale.OtherLotId, l => l.WithdrawnAt = DateTimeOffset.UtcNow);
        var receiptCodes = await api.QueryAsync(db => db.PurchaseReceipts.Select(r => r.Code).ToListAsync());
        var seedLotCodes = await api.QueryAsync(db => db.PurchaseReceiptLines.Select(l => l.Receipt.Code + "-" + l.LineNumber).ToListAsync());

        var raw = await (await api.CreateClient().GetProvenanceAsync(token)).Content.ReadAsStringAsync();

        Assert.Contains(Path.GetFileNameWithoutExtension(publishedPhoto), raw);
        var hidden = new[]
            {
                Path.GetFileNameWithoutExtension(logPhoto), Path.GetFileNameWithoutExtension(withdrawnPhoto), FileCategories.CultivationLogPhotos, "DEMO internal log note",
                sale.Owner.Account.Username, sale.Manager.Account.Username, sale.Owner.Account.Id.ToString(), sale.Manager.Account.Id.ToString(),
                sale.ProductId.ToString(), sale.CustomerId.ToString(), invoice.Id().ToString(), cycleId.ToString(),
            }
            .Concat(receiptCodes).Concat(seedLotCodes);
        Assert.All(hidden, value => Assert.DoesNotContain(value, raw, StringComparison.OrdinalIgnoreCase));
    }

    // BR-FARM-09: the certifications that are not Expired — one with no expiry date and one expiring today — are shown; the one that expired yesterday is not.
    [Fact]
    public async Task Qa_certifications_without_expiry_or_expiring_today_are_shown_and_an_expired_one_is_not()
    {
        using var api = new AuthApiFactory();
        var sale = await api.SaleSetupAsync();
        var (_, token) = await sale.SoldWithQrCodeAsync((sale.LotId, 10m));
        var cooperativeId = await api.QueryAsync(db => db.Cooperatives.Select(c => c.Id).FirstAsync());
        await api.UseDbAsync(db => db.CooperativeCertifications.AddRange(
            new CooperativeCertification { CooperativeId = cooperativeId, Name = "DEMO no expiry" },
            new CooperativeCertification { CooperativeId = cooperativeId, Name = "DEMO expires today", ExpiryDate = Today },
            new CooperativeCertification { CooperativeId = cooperativeId, Name = "DEMO expired yesterday", ExpiryDate = Today.AddDays(-1) }));

        var certifications = (await api.CreateClient().ProvenanceAsync(token)).GetProperty("cooperative").GetProperty("certifications").EnumerateArray().ToList();

        Assert.Equal(["DEMO expires today", "DEMO no expiry"], certifications.Select(c => c.Text("name")).Order(StringComparer.Ordinal));
        Assert.All(certifications, c => Assert.NotEqual("Expired", c.Text("state")));
    }

    // MSG-ORIGIN-01 on a profile saved before the short name and the phone were filled (the bootstrap cooperative has the full name only): the full name, and "–" for the phone,
    // whose parameter stays null so a client building its own text sees there is none.
    [Fact]
    public async Task Qa_a_withdrawn_lot_on_a_profile_without_short_name_or_phone_shows_the_full_name_and_a_dash()
    {
        using var api = new AuthApiFactory();
        var sale = await api.SaleSetupAsync();
        var (_, token) = await sale.SoldWithQrCodeAsync((sale.LotId, 10m));
        var cooperativeId = await api.QueryAsync(db => db.Cooperatives.Select(c => c.Id).FirstAsync());
        await api.UpdateAsync<Cooperative>(cooperativeId, c => (c.FullName, c.ShortName, c.PhoneNumber) = ("DEMO Cooperative full name", null, null));
        await api.UpdateAsync<HarvestLot>(sale.LotId, l => l.WithdrawnAt = DateTimeOffset.UtcNow);
        var lot = await api.LotRowAsync(sale.LotId);
        var date = BusinessClock.ToBusinessDate(lot.WithdrawnAt!.Value).ToString("dd/MM/yyyy", CultureInfo.InvariantCulture);

        var page = await api.CreateClient().ProvenanceAsync(token, "en");

        var shown = page.LotsByCode()[lot.LotCode];
        Assert.Equal($"DEMO Cooperative full name withdrew the origin information of lot {lot.LotCode} on {date}. For details, please call –.", shown.Text("message"));
        Assert.Equal(JsonValueKind.Null, shown.GetProperty("messageParams").GetProperty("cooperative_phone").ValueKind);
        Assert.Equal(JsonValueKind.Null, page.GetProperty("cooperative").GetProperty("phoneNumber").ValueKind);
    }

    // BR-ORIGIN-03: harvest and raft photos are signed links of the two public categories only, in slot order.
    [Fact]
    public async Task Qa_photo_links_are_signed_links_of_harvest_and_raft_photos_only()
    {
        using var api = new AuthApiFactory();
        var (token, before) = await PageWithPhotosAsync(api);

        var (harvest, raft) = PhotoUrls(await api.CreateClient().ProvenanceAsync(token));

        Assert.Equal((2, 1), (harvest.Count, raft.Count));
        Assert.All(harvest, u => Assert.Contains($"{FileCategories.HarvestLotPhotos}/", u));
        Assert.All(raft, u => Assert.Contains($"{FileCategories.RaftPhotos}/", u));
        Assert.All(harvest.Concat(raft), u => Assert.False(string.IsNullOrEmpty(QueryOf(u)["sig"]), u));
        Assert.All(harvest.Concat(raft), u => Assert.True(ExpiryOf(u) > before, u));
    }

    // DEFECT (Low, design §3 "Photo URLs: … Expiry ≥ page lifetime (e.g. 1 h)"): the page is never cached (no-store) and holds no way to refresh one photo, so a photo the
    // browser loads later (a gallery opened, a lazy image scrolled to) must still open an hour after the page; the links expire after 10 minutes.
    [Fact]
    public async Task Photo_links_last_the_one_hour_page_lifetime_the_design_asks_for()
    {
        using var api = new AuthApiFactory();
        var (token, before) = await PageWithPhotosAsync(api);

        var (harvest, raft) = PhotoUrls(await api.CreateClient().ProvenanceAsync(token));

        Assert.All(harvest.Concat(raft), u => Assert.True(ExpiryOf(u) >= before.AddHours(1), $"{u} expires at {ExpiryOf(u):O}, before {before.AddHours(1):O}"));
    }

    // BR-ORIGIN-01: a logged-in user sees the same page — also one whose access token is garbage or expired: the anonymous page is never turned into a 401.
    [Fact]
    public async Task Qa_a_caller_with_a_garbage_access_token_still_gets_the_same_page()
    {
        using var api = new AuthApiFactory();
        var sale = await api.SaleSetupAsync();
        var (_, token) = await sale.SoldWithQrCodeAsync((sale.LotId, 10m));
        var anonymous = await (await api.CreateClient().GetProvenanceAsync(token)).Content.ReadAsStringAsync();
        var garbage = api.CreateClient();
        garbage.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "not.a.valid-token");

        var response = await garbage.GetProvenanceAsync(token);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(anonymous, await response.Content.ReadAsStringAsync());
    }

    // BR-TRACE-05: the 404 of an unknown QR code is not cached either, so a code that a cache once answered unknown is looked up again.
    [Fact]
    public async Task Qa_the_unknown_code_answer_is_not_cached()
    {
        using var api = new AuthApiFactory();

        var response = await api.CreateClient().GetProvenanceAsync("AAAAAAAAAAAAAAAAAAAAAA");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.True(response.Headers.CacheControl?.NoStore, response.Headers.CacheControl?.ToString());
    }

    // The page limit is its own bucket: a client that used up its pages can still log in, and one that used up its logins can still open pages.
    [Fact]
    public async Task Qa_the_page_limit_is_its_own_bucket_separate_from_the_login_limit()
    {
        using var factory = new AuthApiFactory();
        using var api = factory.WithWebHostBuilder(b =>
        {
            b.UseSetting("RateLimits:ProvenancePagesPerMinute", "2");
            b.UseSetting("RateLimits:LoginsPerMinute", "2");
        });
        var client = api.CreateClient();

        var pages = await Task.WhenAll(Enumerable.Range(0, 3).Select(_ => client.GetProvenanceAsync("AAAAAAAAAAAAAAAAAAAAAA")));
        var logins = await Task.WhenAll(Enumerable.Range(0, 2).Select(_ => LoginAsync(client)));

        Assert.Single(pages, r => r.StatusCode == HttpStatusCode.TooManyRequests);
        Assert.All(logins, r => Assert.Equal(HttpStatusCode.Unauthorized, r.StatusCode));
        Assert.Equal(HttpStatusCode.TooManyRequests, (await LoginAsync(client)).StatusCode);
        Assert.Equal(HttpStatusCode.TooManyRequests, (await client.GetProvenanceAsync("AAAAAAAAAAAAAAAAAAAAAA")).StatusCode);
    }

    // Behind a trusted proxy each client gets its own page bucket; from an untrusted address X-Forwarded-For is ignored, so a scanner cannot pick a fresh bucket per request.
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Qa_the_page_limit_sees_the_client_only_through_a_trusted_proxy(bool trusted)
    {
        using var factory = new AuthApiFactory();
        using var api = factory.WithWebHostBuilder(b =>
        {
            b.UseSetting("RateLimits:ProvenancePagesPerMinute", "2");
            b.UseSetting("ForwardedHeaders:KnownNetworks:0", trusted ? "10.0.0.0/8" : "192.168.0.0/16");
            b.ConfigureServices(s => s.AddTransient<IStartupFilter, ProxyConnectionStartupFilter>());
        });
        var client = api.CreateClient();

        var answers = await Task.WhenAll(new[] { "203.0.113.1", "203.0.113.1", "203.0.113.2", "203.0.113.3" }.Select(ip => PageFromAsync(client, ip)));

        Assert.Equal(trusted ? 0 : 2, answers.Count(r => r.StatusCode == HttpStatusCode.TooManyRequests));
    }

    // A sold lot with two harvest photos and a raft photo, its QR code and the time before the page is asked.
    private static async Task<(string Token, DateTimeOffset Before)> PageWithPhotosAsync(AuthApiFactory api)
    {
        var sale = await api.SaleSetupAsync();
        await api.UseDbAsync(db =>
        {
            db.HarvestLotPhotos.Add(new HarvestLotPhoto { LotId = sale.LotId, SortOrder = 1, FileKey = $"{FileCategories.HarvestLotPhotos}/2026/11/{Guid.NewGuid():N}.jpg" });
            db.HarvestLotPhotos.Add(new HarvestLotPhoto { LotId = sale.LotId, SortOrder = 2, FileKey = $"{FileCategories.HarvestLotPhotos}/2026/11/{Guid.NewGuid():N}.jpg" });
            db.RaftPhotos.Add(new RaftPhoto { RaftId = sale.RaftId, SortOrder = 1, FileKey = $"{FileCategories.RaftPhotos}/2026/11/{Guid.NewGuid():N}.jpg" });
        });
        var (_, token) = await sale.SoldWithQrCodeAsync((sale.LotId, 10m));
        return (token, DateTimeOffset.UtcNow.AddSeconds(-1));
    }

    private static (List<string> Harvest, List<string> Raft) PhotoUrls(JsonElement page)
    {
        var origin = page.GetProperty("lots")[0].GetProperty("origin");
        return (origin.GetProperty("harvestPhotoUrls").EnumerateArray().Select(u => u.GetString()!).ToList(),
            origin.GetProperty("raftPhotoUrls").EnumerateArray().Select(u => u.GetString()!).ToList());
    }

    private static System.Collections.Specialized.NameValueCollection QueryOf(string url) => System.Web.HttpUtility.ParseQueryString(url[(url.IndexOf('?') + 1)..]);

    // The Local storage link carries its expiry as Unix seconds (LocalFileUrlSigner).
    private static DateTimeOffset ExpiryOf(string url) => DateTimeOffset.FromUnixTimeSeconds(long.Parse(QueryOf(url)["expires"]!, CultureInfo.InvariantCulture));

    private static Task<HttpResponseMessage> LoginAsync(HttpClient client) =>
        client.PostAsJsonAsync("/api/auth/login", new { identifier = AcsTestKit.NewCode(), password = AcsTestKit.NewPassword() });

    private static Task<HttpResponseMessage> PageFromAsync(HttpClient client, string clientAddress)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, "/api/public/provenance/AAAAAAAAAAAAAAAAAAAAAA");
        request.Headers.Add("X-Forwarded-For", clientAddress);
        return client.SendAsync(request);
    }

    // The test server has no connection address; every request comes "from the proxy" 10.0.0.5.
    private sealed class ProxyConnectionStartupFilter : IStartupFilter
    {
        public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => app =>
        {
            app.Use((context, nextMiddleware) =>
            {
                context.Connection.RemoteIpAddress = IPAddress.Parse("10.0.0.5");
                return nextMiddleware(context);
            });
            next(app);
        };
    }
}
