using System.Net;
using OMTS.Api.IntegrationTests.Cultivation;
using OMTS.Api.IntegrationTests.Farming;
using OMTS.Api.IntegrationTests.Harvest;
using OMTS.Api.IntegrationTests.Infrastructure;
using static OMTS.Api.IntegrationTests.Farming.FarmingTestKit;
using static OMTS.Api.IntegrationTests.Sales.SalesInvoiceTestKit;

namespace OMTS.Api.IntegrationTests.Traceability;

/// <summary>QA of the TRACE-02.0 step 1 search on SQL Server: LIKE wildcards and SQL-like text are plain text, case does not matter, long input is no error, zones stay apart.</summary>
public class LotTraceQaTestsSqlServer
{
    [SqlServerFact]
    public async Task The_search_reads_wildcards_and_sql_like_input_as_plain_text_ignores_case_and_never_crosses_zones()
    {
        await using var host = await FarmingSqlServerHost.StartAsync();
        var owner = host.Owner.Client;
        var zoneId = (await owner.CreateZoneAsync()).Id();
        var raftId = (await owner.CreateRaftAsync(zoneId)).Id();
        var cycleId = (await owner.StartCycleAsync(raftId, new CycleBody(Today.AddDays(-10)))).Id();
        await owner.SeedingRecordAsync(cycleId, (await owner.SeedLotAsync()).Session([1, 2, 3, 4, 5]));
        var lotId = (await owner.HarvestLotAsync(raftId, HarvestFields.OfLanes(1))).Id();
        var productId = (await Sales.SalesCatalogueTestKit.CreateProductAsync(owner, unitPrice: ListPrice)).Id();
        var manager = (await host.StaffInZoneAsync(zoneId)).Client;
        var stranger = (await host.StaffInZoneAsync((await owner.CreateZoneAsync()).Id())).Client;
        var invoiceCode = (await manager.SellAsync(Invoice(null, [Line(productId, lotId, 1m)], Cash(ListPrice), lotId))).GetProperty("invoice").Text("code");
        var code = (await manager.TraceAsync(lotId)).Text("lotCode");

        Assert.Equal([lotId], await manager.FoundAsync(lotCode: code.ToLowerInvariant()));
        Assert.Equal([lotId], await manager.FoundAsync(lotCode: code[..3]));
        Assert.Equal([lotId], await manager.FoundAsync(invoiceCode: invoiceCode.ToLowerInvariant()));

        foreach (var (lotCode, invoice) in new (string?, string?)[]
                 {
                     ("%", null), ("_" + code[1..], null), ($"[{code[0]}]{code[1..]}", null), ("' OR 1=1 --", null), (new string('K', 300), null),
                     (null, "%"), (null, "HD%"), (null, "' OR ''='"), (code, invoiceCode + "%"),
                 })
        {
            var answer = await manager.SearchTraceAsync(lotCode, invoice);
            var body = await answer.ReadAsync(HttpStatusCode.OK);
            Assert.Equal((0, "MSG-GEN-10"), (body.GetProperty("lots").GetArrayLength(), body.Text("messageCode")));
        }

        // TRACE-02.0.E1: another zone's Zone Manager finds nothing by the same prefix or invoice.
        Assert.Empty(await stranger.FoundAsync(lotCode: code[..3]));
        Assert.Empty(await stranger.FoundAsync(invoiceCode: invoiceCode));
    }
}
