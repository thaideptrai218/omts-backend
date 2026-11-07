using System.Net;
using OMTS.Api.IntegrationTests.Cultivation;
using OMTS.Api.IntegrationTests.Farming;
using OMTS.Api.IntegrationTests.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using OMTS.Application.Common;
using OMTS.Application.Common.Interfaces.Persistence;
using OMTS.Application.Features.Harvest;
using OMTS.Domain.Constants;
using OMTS.Domain.Entities.Farming;
using static OMTS.Api.IntegrationTests.Sales.SalesInvoiceTestKit;

namespace OMTS.Api.IntegrationTests.Harvest;

/// <summary>QA of PR H2, HARVEST-02 by hand: the proposed leftover, who closes (trader, All-zones Zone Manager), a discontinued raft or zone, and what a closed lot answers afterwards.</summary>
public class HarvestLotCloseQaTests
{
    // HARVEST-02.0 step 2: "the system proposes the remaining kg". A sale weighs kg with 3 decimals (SALE-05, InvoiceMoney.IsKg), so the remaining kg of BR-HARVEST-07 has 3 decimals too;
    // the Zone Manager who keeps the proposed value must be able to close with it, as the daily check (GFR-05) does with the same figure.
    [Fact]
    public async Task The_proposed_remaining_kg_of_a_lot_sold_with_three_decimals_is_accepted_as_the_leftover()
    {
        using var api = new AuthApiFactory();
        var sale = await api.SaleSetupAsync();
        await sale.Manager.Client.SellAsync(Invoice(sale.CustomerId, [Line(sale.ProductId, sale.LotId, 12.345m)], Cash(370_350m), sale.LotId));
        var proposed = (await sale.Manager.Client.GetHarvestLotAsync(sale.LotId)).GetProperty("figures").Kg("remainingKg");
        Assert.Equal(387.655m, proposed);

        var answer = await api.SendCloseAsync(sale.Manager.Client, sale.LotId, proposed, "Unsold");

        Assert.True(answer.StatusCode == HttpStatusCode.OK, $"{(int)answer.StatusCode}: {await answer.Content.ReadAsStringAsync()}");
        Assert.Equal(387.655m, (await api.LotRowAsync(sale.LotId)).LeftoverKg);
    }

    // §I.4.4: a trader does not use HARVEST-02; an All-zones Zone Manager is a Zone Manager of every zone (BR-FARM-08) and closes like one.
    [Fact]
    public async Task A_trader_is_refused_and_an_all_zones_zone_manager_closes_the_lot()
    {
        using var api = new AuthApiFactory();
        var raft = await api.SeededRaftAsync();
        var lot = await raft.Owner.Client.HarvestLotAsync(raft.RaftId);
        var trader = await FarmingTestKit.TraderAsync(api);
        var allZones = await api.StaffAsync(null);

        Assert.Equal("MSG-GEN-08", await (await api.SendCloseAsync(trader, lot.Id(), 0m)).ErrorCodeAsync());
        Assert.Equal(StatusIds.HarvestLot.Open, (await api.LotRowAsync(lot.Id())).StatusId);
        Assert.True((await allZones.Client.GetHarvestLotAsync(lot.Id())).GetProperty("canClose").GetBoolean());

        var closed = (await api.CloseLotAsync(allZones.Client, lot.Id(), 2.5m, "Spoiled")).GetProperty("lot");

        Assert.Equal(("Closed", "Spoiled"), (closed.Text("status"), closed.Text("closeReason")));
        Assert.Equal(allZones.Account.Id, Assert.Single(await api.CloseRowsAsync(lot.Id())).ActorAccountId);
    }

    // Developer deviation 4 (PM question 1): a lot of a discontinued raft, or of a raft of a discontinued zone, is still for sale, so it is closed by hand as it is sold.
    [Fact]
    public async Task A_lot_of_a_discontinued_raft_or_zone_is_still_closed_by_its_zone_manager()
    {
        using var api = new AuthApiFactory();
        var raft = await api.SeededRaftAsync();
        var manager = await api.ManagerAsync(raft.ZoneId);
        var onRaft = await manager.Client.HarvestLotAsync(raft.RaftId, HarvestFields.OfLanes(1));
        var onZone = await manager.Client.HarvestLotAsync(raft.RaftId, HarvestFields.OfLanes(2));
        await api.DiscontinueRaftAsync(raft.RaftId);
        Assert.True((await manager.Client.GetHarvestLotAsync(onRaft.Id())).GetProperty("canClose").GetBoolean());

        Assert.Equal("Closed", (await api.CloseLotAsync(manager.Client, onRaft.Id(), 0m)).GetProperty("lot").Text("status"));

        await api.UpdateAsync<FarmingZone>(raft.ZoneId, z => z.StatusId = StatusIds.Usage.Discontinued);
        Assert.Equal("Closed", (await api.CloseLotAsync(raft.Owner.Client, onZone.Id(), 1m, "Unsold")).GetProperty("lot").Text("status"));
    }

    // POST-1: a closed lot can no longer be chosen on a sales invoice (SALE-05.0.E2, MSG-GEN-09 naming it) and the Owner's for-sale count drops; a leftover above the remaining kg is
    // not refused (the SRS sets no such limit: BR-HARVEST-02 makes the remaining kg information only).
    [Fact]
    public async Task A_lot_closed_with_a_leftover_above_its_remaining_kg_is_refused_on_a_new_sale_with_msg_gen_09()
    {
        using var api = new AuthApiFactory();
        var sale = await api.SaleSetupAsync();
        var manager = sale.Manager.Client;
        Assert.Equal(2, await sale.Owner.Client.ForSaleCountAsync());

        await api.CloseLotAsync(manager, sale.LotId, 450m, "Spoiled");

        Assert.Equal(1, await sale.Owner.Client.ForSaleCountAsync());
        Assert.Equal([sale.OtherLotId.ToString()], (await manager.LotsForSaleAsync()).GetProperty("lots").EnumerateArray().Select(l => l.GetProperty("id").GetString()!).ToList());
        var refused = await (await manager.PostInvoiceAsync(Invoice(sale.CustomerId, [Line(sale.ProductId, sale.LotId, 10m)], Cash(300_000m)))).ReadAsync(HttpStatusCode.Conflict);
        Assert.Equal("MSG-GEN-09", refused.Text("code"));
        Assert.Contains("is Closed", refused.Text("detail"));
    }

    // HARVEST-02.1 hook: on a lot already Closed it answers MSG-GEN-09 and changes nothing, so the sales return that calls it can still be saved without the close.
    [Fact]
    public async Task The_sales_return_hook_on_a_closed_lot_answers_msg_gen_09_and_leaves_the_lot_as_it_was()
    {
        using var api = new AuthApiFactory();
        var raft = await api.SeededRaftAsync();
        var manager = await api.ManagerAsync(raft.ZoneId);
        var lot = await manager.Client.HarvestLotAsync(raft.RaftId);
        await api.CloseLotAsync(manager.Client, lot.Id(), 4m, "Unsold");

        using var scope = api.Services.CreateScope();
        var services = scope.ServiceProvider;
        var tracked = await services.GetRequiredService<IHarvestLotRepository>().GetForEditAsync(lot.Id(), CancellationToken.None);
        using var acting = services.GetRequiredService<AmbientActor>().Use(manager.Account.Id);

        var refused = await services.GetRequiredService<HarvestLotClosingService>().CloseWithoutSavingAsync(tracked!, 9m, "Spoiled", CancellationToken.None);
        await services.GetRequiredService<IUnitOfWork>().SaveChangesAsync();

        Assert.Equal("MSG-GEN-09", refused!.Message.Code);
        var row = await api.LotRowAsync(lot.Id());
        Assert.Equal((4m, OMTS.Domain.Enums.HarvestLotCloseReason.Unsold), (row.LeftoverKg!.Value, row.CloseReason!.Value));
        Assert.Single(await api.CloseRowsAsync(lot.Id()));
    }
}
