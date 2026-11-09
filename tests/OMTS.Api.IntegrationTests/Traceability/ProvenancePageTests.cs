using System.Globalization;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using OMTS.Api.IntegrationTests.Cultivation;
using OMTS.Api.IntegrationTests.Farming;
using OMTS.Api.IntegrationTests.Harvest;
using OMTS.Api.IntegrationTests.Infrastructure;
using OMTS.Application.Common;
using OMTS.Domain.Entities.Harvest;
using OMTS.Domain.Enums;
using static OMTS.Api.IntegrationTests.Farming.FarmingTestKit;
using static OMTS.Api.IntegrationTests.Sales.SalesInvoiceTestKit;
using Catalogue = OMTS.Api.IntegrationTests.Sales.SalesCatalogueTestKit;
using SaleSetup = OMTS.Api.IntegrationTests.Sales.SaleSetup;

namespace OMTS.Api.IntegrationTests.Traceability;

/// <summary>ORIGIN-01.0: the lots a provenance page lists (AC-05, BR-ORIGIN-05), a Withdrawn lot (ORIGIN-01.1) and the current data after a correction (BR-TRACE-05).</summary>
public class ProvenancePageTests
{
    // AC-05: two harvest trips to rafts B01 and B02 of one zone make two lots; one invoice of 300 kg and 200 kg gets one QR code, whose page lists both Published lots.
    [Fact]
    public async Task Ac_05_one_qr_code_lists_both_lots_of_the_invoice_from_two_rafts()
    {
        using var api = new AuthApiFactory();
        var sale = await api.SaleSetupAsync();
        var owner = sale.Owner.Client;
        var secondRaftId = (await owner.CreateRaftAsync(sale.ZoneId)).Id();
        var cycle = await owner.StartCycleAsync(secondRaftId, new CycleBody(Today.AddDays(-10)));
        var seed = await owner.SeedLotAsync();
        await owner.SeedingRecordAsync(cycle.Id(), seed.Session([1, 2, 3, 4, 5]));
        var secondLot = await owner.HarvestLotAsync(secondRaftId, HarvestFields.OfLanes(1));

        var (invoice, token) = await sale.SoldWithQrCodeAsync((sale.LotId, 300m), (secondLot.Id(), 200m));

        var page = await api.CreateClient().ProvenanceAsync(token);
        Assert.Equal(("vi", "Ok", invoice.Text("code"), invoice.Date("invoiceDate")), (page.Text("language"), page.Text("status"), page.GetProperty("invoice").Text("code"),
            page.GetProperty("invoice").Date("date")));
        var lots = page.GetProperty("lots").EnumerateArray().ToList();
        var firstCode = (await api.LotRowAsync(sale.LotId)).LotCode;
        Assert.Equal([firstCode, secondLot.Text("lotCode")], lots.Select(l => l.Text("lotCode")));
        Assert.All(lots, l => Assert.Equal("Published", l.Text("originState")));
        var rafts = await api.QueryAsync(db => db.Rafts.Where(r => r.Id == sale.RaftId || r.Id == secondRaftId).ToDictionaryAsync(r => r.Id, r => r.Code));
        Assert.Equal([rafts[sale.RaftId], rafts[secondRaftId]], lots.Select(l => l.GetProperty("origin").Text("raftCode")));
        Assert.All(lots, l => Assert.Equal(JsonValueKind.Null, l.GetProperty("message").ValueKind));
    }

    // BR-ORIGIN-05: a lot that is Not published is not listed; the page does not say why.
    [Fact]
    public async Task A_lot_that_is_not_published_is_not_listed()
    {
        using var api = new AuthApiFactory();
        var sale = await api.SaleSetupAsync();
        await api.LeaveLaneWithoutSeedLotAsync(sale.RaftId, 2);

        var (_, token) = await sale.SoldWithQrCodeAsync((sale.LotId, 10m), (sale.OtherLotId, 5m));

        Assert.Equal(HarvestLotOriginState.NotPublished, (await api.LotRowAsync(sale.OtherLotId)).OriginState);
        var page = await api.CreateClient().ProvenanceAsync(token);
        Assert.Equal([(await api.LotRowAsync(sale.LotId)).LotCode], page.LotsByCode().Keys);
        Assert.Equal("Ok", page.Text("status"));

        // ORIGIN-01.2: once no lot is listed (here the Published lot is taken back to Not published, as an invoice correction would leave it), MSG-ORIGIN-02.
        await api.UpdateAsync<HarvestLot>(sale.LotId, l => l.PublishedAt = null);
        var none = await api.CreateClient().ProvenanceAsync(token);
        Assert.Equal(("NoLots", "MSG-ORIGIN-02", 0), (none.Text("status"), none.Text("messageCode"), none.GetProperty("lots").GetArrayLength()));
        Assert.NotEqual(JsonValueKind.Null, none.GetProperty("invoice").ValueKind);
    }

    // ORIGIN-01.1: a Withdrawn lot is listed with the warning and its withdrawal date, without origin information and without the reason; the other lot keeps its origin.
    [Fact]
    public async Task A_withdrawn_lot_shows_the_warning_with_the_date_only()
    {
        using var api = new AuthApiFactory();
        var sale = await api.SaleSetupAsync();
        var profile = await api.CooperativeProfileAsync();
        var (_, token) = await sale.SoldWithQrCodeAsync((sale.LotId, 10m), (sale.OtherLotId, 5m));
        await api.UpdateAsync<HarvestLot>(sale.OtherLotId, l => (l.WithdrawnAt, l.WithdrawalReason) = (DateTimeOffset.UtcNow, "DEMO internal withdrawal reason"));
        var withdrawn = await api.LotRowAsync(sale.OtherLotId);
        var date = BusinessClock.ToBusinessDate(withdrawn.WithdrawnAt!.Value);

        var response = await api.CreateClient().GetProvenanceAsync(token);
        var raw = await response.Content.ReadAsStringAsync();
        var page = JsonDocument.Parse(raw).RootElement;

        var lots = page.LotsByCode();
        var lot = lots[withdrawn.LotCode];
        Assert.Equal(("Withdrawn", date, "MSG-ORIGIN-01"), (lot.Text("originState"), lot.Date("withdrawalDate"), lot.Text("messageCode")));
        Assert.Equal(
            $"{profile.ShortName} đã thu hồi thông tin nguồn gốc của lô {withdrawn.LotCode} vào ngày {date.ToString("dd/MM/yyyy", CultureInfo.InvariantCulture)}. Để biết thêm chi tiết, vui lòng gọi {profile.Phone}.",
            lot.Text("message"));
        Assert.Equal(JsonValueKind.Null, lot.GetProperty("origin").ValueKind);
        var parameters = lot.GetProperty("messageParams");
        Assert.Equal((profile.ShortName, withdrawn.LotCode, date, profile.Phone), (parameters.Text("cooperative_short_name"), parameters.Text("lot_code"),
            parameters.Date("withdrawal_date"), parameters.Text("cooperative_phone")));
        Assert.DoesNotContain("DEMO internal withdrawal reason", raw);
        Assert.Equal("Published", lots[(await api.LotRowAsync(sale.LotId)).LotCode].Text("originState"));

        // A Withdrawn lot published again by the Owner (BR-TRACE-02) shows its origin information at once (BR-ORIGIN-05).
        await api.UpdateAsync<HarvestLot>(sale.OtherLotId, l => l.PublishedAt = DateTimeOffset.UtcNow.AddSeconds(1));
        var again = (await api.CreateClient().ProvenanceAsync(token)).LotsByCode()[withdrawn.LotCode];
        Assert.Equal(("Published", JsonValueKind.Object), (again.Text("originState"), again.GetProperty("origin").ValueKind));
    }

    // BR-TRACE-05: no publication versions, so a TRACE-03 correction of the seeding date shows on the page at once, with the days of farming worked out again.
    [Fact]
    public async Task A_trace_correction_shows_on_the_page_at_once()
    {
        using var api = new AuthApiFactory();
        var raft = await api.SeededRaftAsync();
        var owner = raft.Owner.Client;
        var lot = await owner.HarvestLotAsync(raft.RaftId, HarvestFields.OfLanes(1));
        var sale = new SaleSetup(raft.Owner, raft.Owner, raft.ZoneId, raft.RaftId, lot.Id(), lot.Id(),
            (await Catalogue.CreateProductAsync(owner, unitPrice: ListPrice)).Id(), (await Catalogue.CreateCustomerAsync(owner)).Id());
        var (_, token) = await sale.SoldWithQrCodeAsync((lot.Id(), 10m));
        var item = await api.QueryAsync(db => db.FarmingInputItems.Where(i => i.Id == raft.Lot.ItemId).Select(i => i.Name).SingleAsync());

        var before = (await api.CreateClient().ProvenanceAsync(token)).GetProperty("lots")[0].GetProperty("origin");
        var seeding = Assert.Single(before.GetProperty("seedings").EnumerateArray());
        Assert.Equal((Today.AddDays(-1), item, 1), (seeding.Date("seedingDate"), seeding.Text("seedItemName"), before.GetProperty("daysOfFarming").GetInt32()));

        (await owner.PutSeedingTraceAsync(raft.Session.Id(), raft.Session.SeedingCorrection(date: Today.AddDays(-4)))).EnsureSuccessStatusCode();

        var after = (await api.CreateClient().ProvenanceAsync(token)).GetProperty("lots")[0];
        Assert.Equal("Published", after.Text("originState"));
        Assert.Equal(Today.AddDays(-4), Assert.Single(after.GetProperty("origin").GetProperty("seedings").EnumerateArray()).Date("seedingDate"));
        Assert.Equal(4, after.GetProperty("origin").GetProperty("daysOfFarming").GetInt32());
    }
}
