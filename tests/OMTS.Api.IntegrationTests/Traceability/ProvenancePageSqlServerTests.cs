using System.Net;
using Microsoft.EntityFrameworkCore;
using OMTS.Api.IntegrationTests.Cultivation;
using OMTS.Api.IntegrationTests.Farming;
using OMTS.Api.IntegrationTests.Harvest;
using OMTS.Api.IntegrationTests.Infrastructure;
using OMTS.Application.Features.Files;
using OMTS.Domain.Constants;
using OMTS.Domain.Entities.Farming;
using OMTS.Domain.Entities.Harvest;
using static OMTS.Api.IntegrationTests.Farming.FarmingTestKit;
using static OMTS.Api.IntegrationTests.Sales.SalesInvoiceSendingTestKit;
using static OMTS.Api.IntegrationTests.Sales.SalesInvoiceTestKit;
using Catalogue = OMTS.Api.IntegrationTests.Sales.SalesCatalogueTestKit;

namespace OMTS.Api.IntegrationTests.Traceability;

/// <summary>ORIGIN-01 on SQL Server: the page reads translate, and only the exact QR code opens the page although SQL Server compares text without case (BR-TRACE-02).</summary>
public class ProvenancePageSqlServerTests
{
    [SqlServerFact]
    public async Task The_page_reads_its_lots_on_sql_server_and_only_the_exact_qr_code_opens_it()
    {
        await using var host = await FarmingSqlServerHost.StartAsync();
        var owner = host.Owner.Client;
        var zoneId = (await owner.CreateZoneAsync()).Id();
        var raftId = (await owner.CreateRaftAsync(zoneId)).Id();
        var cycleId = (await owner.StartCycleAsync(raftId, new CycleBody(Today.AddDays(-10)))).Id();
        var seed = await owner.SeedLotAsync();
        await owner.SeedingRecordAsync(cycleId, seed.Session([1, 2, 3, 4, 5], date: Today.AddDays(-3)));
        await owner.SeedingRecordAsync(cycleId, seed.Session([2], date: Today.AddDays(-1)));
        var productId = (await Catalogue.CreateProductAsync(owner, unitPrice: ListPrice)).Id();
        var customerId = (await Catalogue.CreateCustomerAsync(owner)).Id();
        var lotId = (await owner.HarvestLotAsync(raftId, HarvestFields.OfLanes(1, 2))).Id();
        var withdrawnId = (await owner.HarvestLotAsync(raftId, HarvestFields.OfLanes(3))).Id();
        await host.UseDbAsync(db =>
        {
            db.HarvestLotPhotos.Add(new HarvestLotPhoto { LotId = lotId, SortOrder = 1, FileKey = $"{FileCategories.HarvestLotPhotos}/2026/11/{Guid.NewGuid():N}.jpg" });
            db.RaftPhotos.Add(new RaftPhoto { RaftId = raftId, SortOrder = 1, FileKey = $"{FileCategories.RaftPhotos}/2026/11/{Guid.NewGuid():N}.jpg" });
        });
        var invoice = (await owner.SellAsync(Invoice(customerId, [Line(productId, lotId, 3m), Line(productId, withdrawnId, 2m)], Cash(5m * ListPrice), lotId, withdrawnId)))
            .GetProperty("invoice");
        var token = (await owner.FinishedAsync(invoice.Id(), wantsQrCode: true)).Text("qrUrl").Split('/')[^1];
        await host.UseDbAsync(db => db.HarvestLots.Where(l => l.Id == withdrawnId).ExecuteUpdate(s => s.SetProperty(l => l.WithdrawnAt, DateTimeOffset.UtcNow)));
        var item = await host.QueryAsync(db => db.FarmingInputItems.Where(i => i.Id == seed.ItemId).Select(i => i.Name).SingleAsync());
        var anonymous = host.Api.CreateClient();

        var page = await anonymous.ProvenanceAsync(token, "en");

        Assert.Equal(("Ok", invoice.Text("code")), (page.Text("status"), page.GetProperty("invoice").Text("code")));
        var lots = page.GetProperty("lots").EnumerateArray().ToList();
        Assert.Equal(["Published", "Withdrawn"], lots.Select(l => l.Text("originState")));
        var origin = lots[0].GetProperty("origin");
        Assert.Equal([(Today.AddDays(-3), item), (Today.AddDays(-1), item)],
            origin.GetProperty("seedings").EnumerateArray().Select(s => (s.Date("seedingDate"), s.Text("seedItemName"))));
        Assert.Equal((3, 1, 1), (origin.GetProperty("daysOfFarming").GetInt32(), origin.GetProperty("harvestPhotoUrls").GetArrayLength(),
            origin.GetProperty("raftPhotoUrls").GetArrayLength()));
        Assert.Equal("MSG-ORIGIN-01", lots[1].Text("messageCode"));

        var otherCase = new string(token.Select(c => char.IsUpper(c) ? char.ToLowerInvariant(c) : char.ToUpperInvariant(c)).ToArray());
        if (otherCase != token)
        {
            await (await anonymous.GetProvenanceAsync(otherCase)).SrsProblemAsync(HttpStatusCode.NotFound, "MSG-ORIGIN-03", null);
        }

        await host.UseDbAsync(db => db.SalesInvoices.Where(i => i.Id == invoice.Id()).ExecuteUpdate(s => s.SetProperty(i => i.StatusId, StatusIds.SalesInvoice.Cancelled)));
        Assert.Equal("Cancelled", (await anonymous.ProvenanceAsync(token)).Text("status"));
    }
}
