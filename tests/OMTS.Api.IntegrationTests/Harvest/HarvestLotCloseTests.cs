using System.Net;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using OMTS.Api.IntegrationTests.Cultivation;
using OMTS.Api.IntegrationTests.Farming;
using OMTS.Api.IntegrationTests.Infrastructure;
using OMTS.Application.Common;
using OMTS.Application.Common.Interfaces.Persistence;
using OMTS.Application.Features.Harvest;
using OMTS.Domain.Constants;
using OMTS.Domain.Enums;
using static OMTS.Api.IntegrationTests.Harvest.HarvestLotCloseTestKit;

namespace OMTS.Api.IntegrationTests.Harvest;

/// <summary>HARVEST-02: who closes a lot, the leftover kg and reason (HARVEST-02.0.E1), the audit row, and the CLAIM-03 extension point.</summary>
public class HarvestLotCloseTests
{
    private static List<string> Fields(JsonElement problem) => problem.GetProperty("fields").EnumerateArray().Select(f => f.GetString()!).ToList();

    // HARVEST-02.0: any Zone Manager of the zone, not only who recorded the lot; today is the close date; no notification (MSG-GEN-05 only).
    [Fact]
    public async Task A_zone_manager_of_the_zone_closes_a_lot_another_recorded_and_the_audit_keeps_before_and_after()
    {
        using var api = new AuthApiFactory();
        var raft = await api.SeededRaftAsync();
        var lot = await raft.Owner.Client.HarvestLotAsync(raft.RaftId);
        var manager = await api.ManagerAsync(raft.ZoneId);
        var read = await manager.Client.GetHarvestLotAsync(lot.Id());
        Assert.Equal((true, false, 400m), (read.GetProperty("canClose").GetBoolean(), read.GetProperty("canCorrect").GetBoolean(), read.GetProperty("figures").Kg("remainingKg")));
        Assert.True((await manager.Client.HarvestScreenAsync(raft.RaftId)).GetProperty("lots")[0].GetProperty("canClose").GetBoolean());

        var saved = await api.CloseLotAsync(manager.Client, lot.Id(), 12.5m, " spoiled ");

        Assert.Equal(("MSG-GEN-05", "Harvest lot saved."), (saved.Text("messageCode"), saved.Text("message")));
        var closed = saved.GetProperty("lot");
        Assert.Equal(("Closed", "Spoiled", 12.5m, FarmingTestKit.Today), (closed.Text("status"), closed.Text("closeReason"), closed.Kg("leftoverKg"), closed.Date("closeDate")));
        Assert.Equal((false, 12.5m, false), (closed.GetProperty("canClose").GetBoolean(), closed.GetProperty("figures").Kg("lostKg"), closed.GetProperty("figures").GetProperty("notFinal").GetBoolean()));
        Assert.DoesNotContain(lot.Text("lotCode"), (await manager.Client.LotsForSaleAsync()).ForSaleCodes());

        var row = Assert.Single(await api.CloseRowsAsync(lot.Id()));
        Assert.Equal((manager.Account.Id, raft.ZoneId, lot.Text("lotCode")), (row.ActorAccountId!.Value, row.ZoneId!.Value, row.RecordCode));
        using var before = JsonDocument.Parse(row.Before!);
        using var after = JsonDocument.Parse(row.After!);
        Assert.Equal(JsonValueKind.Null, before.RootElement.GetProperty("LeftoverKg").ValueKind);
        Assert.Equal(("CLOSED", 12.5m, "Spoiled"), (after.RootElement.Text("Status"), after.RootElement.GetProperty("LeftoverKg").GetDecimal(), after.RootElement.Text("CloseReason")));
        Assert.Empty(await api.QueryAsync(db => db.Notifications.Where(n => n.RelatedRecordId == lot.Id() && n.Type != "HARVEST_LOT_RECORDED").ToListAsync()));
    }

    // Q3 default: a close with no leftover keeps no reason, even one that was sent.
    [Fact]
    public async Task A_leftover_of_0_closes_without_a_reason_and_the_owner_closes_any_lot()
    {
        using var api = new AuthApiFactory();
        var raft = await api.SeededRaftAsync();
        var manager = await api.ManagerAsync(raft.ZoneId);
        var lot = await manager.Client.HarvestLotAsync(raft.RaftId);

        var closed = (await api.CloseLotAsync(raft.Owner.Client, lot.Id(), 0m, "Unsold")).GetProperty("lot");

        Assert.Equal(("Closed", 0m, JsonValueKind.Null), (closed.Text("status"), closed.Kg("leftoverKg"), closed.GetProperty("closeReason").ValueKind));
        Assert.Equal(raft.Owner.Account.Id, Assert.Single(await api.CloseRowsAsync(lot.Id())).ActorAccountId);
    }

    [Fact]
    public async Task Missing_or_invalid_leftover_and_reason_are_named_and_nothing_is_saved()
    {
        using var api = new AuthApiFactory();
        var raft = await api.SeededRaftAsync();
        var owner = raft.Owner.Client;
        var lot = await owner.HarvestLotAsync(raft.RaftId);

        foreach (var (leftover, reason, field) in new (decimal?, string?, string)[]
        {
            (null, "Unsold", "Leftover kg"), (-1m, "Unsold", "Leftover kg"), (1.2345m, "Unsold", "Leftover kg"), (1_000_000_001m, "Unsold", "Leftover kg"),
            (5m, null, "Close reason"), (5m, " ", "Close reason"), (5m, "SoldWholeLot", "Close reason"), (5m, "TimeLimitReached", "Close reason"), (5m, "Lost", "Close reason"),
        })
        {
            var problem = await (await api.SendCloseAsync(owner, lot.Id(), leftover, reason)).ReadAsync(HttpStatusCode.BadRequest);
            Assert.Equal(("MSG-GEN-01", field), (problem.Text("code"), problem.Text("field")));
            Assert.Equal([field], Fields(problem));
        }

        Assert.Equal("RowVersion", (await (await owner.PostCloseLotAsync(lot.Id(), 0m, null, null)).ReadAsync(HttpStatusCode.BadRequest)).Text("field"));
        Assert.Equal(StatusIds.HarvestLot.Open, (await api.LotRowAsync(lot.Id())).StatusId);
        Assert.Empty(await api.CloseRowsAsync(lot.Id()));
    }

    // §I.4.4: HARVEST-02 is Full for the Owner, Full* (zone) for a Zone Manager, No for an Accountant. The zone is checked before the status.
    [Fact]
    public async Task An_accountant_and_a_zone_manager_of_another_zone_are_refused_and_change_nothing()
    {
        using var api = new AuthApiFactory();
        var raft = await api.SeededRaftAsync();
        var lot = await raft.Owner.Client.HarvestLotAsync(raft.RaftId);
        var accountant = await api.StaffAsync(null, StaffRole.Accountant);
        var otherZone = await api.ManagerAsync((await raft.Owner.Client.CreateZoneAsync()).Id());

        foreach (var client in new[] { accountant.Client, otherZone.Client })
        {
            Assert.Equal("MSG-GEN-08", await (await api.SendCloseAsync(client, lot.Id(), 0m)).ErrorCodeAsync());
        }

        Assert.Equal(HttpStatusCode.Forbidden, (await otherZone.Client.GetAsync($"/api/harvest-lots/{lot.Id()}")).StatusCode);
        Assert.Equal(StatusIds.HarvestLot.Open, (await api.LotRowAsync(lot.Id())).StatusId);

        await api.CloseLotAsync(raft.Owner.Client, lot.Id(), 0m);
        Assert.Equal("MSG-GEN-08", await (await api.SendCloseAsync(otherZone.Client, lot.Id(), 0m)).ErrorCodeAsync());
    }

    // BR-HARVEST-04: only a lot for sale is closed; a Reopened lot is closed again in the same way.
    [Fact]
    public async Task A_closed_lot_answers_msg_gen_09_and_a_reopened_lot_is_closed_again()
    {
        using var api = new AuthApiFactory();
        var raft = await api.SeededRaftAsync();
        var owner = raft.Owner.Client;
        var lot = await owner.HarvestLotAsync(raft.RaftId);
        await api.CloseLotAsync(owner, lot.Id(), 0m);

        var refused = await (await api.SendCloseAsync(owner, lot.Id(), 0m)).ReadAsync(HttpStatusCode.Conflict);
        Assert.Equal(("MSG-GEN-09", $"Harvest lot {lot.Text("lotCode")} is Closed and cannot be used for this action."), (refused.Text("code"), refused.Text("detail")));
        Assert.False((await owner.GetHarvestLotAsync(lot.Id())).GetProperty("canClose").GetBoolean());
        Assert.Equal(HttpStatusCode.NotFound, (await owner.PostCloseLotAsync(Guid.NewGuid(), 0m, null, "")).StatusCode);

        await api.ReopenAsync(lot.Id(), FarmingTestKit.Today);
        Assert.True((await owner.GetHarvestLotAsync(lot.Id())).GetProperty("canClose").GetBoolean());

        var again = (await api.CloseLotAsync(owner, lot.Id(), 3m, "Unsold")).GetProperty("lot");
        Assert.Equal(("Closed", "Unsold"), (again.Text("status"), again.Text("closeReason")));
        Assert.Equal(2, (await api.CloseRowsAsync(lot.Id())).Count);
    }

    // BR-HARVEST-04 end: selling and closing go on after the cycle is closed, while the harvest data is locked (BR-CYCLE-04).
    [Fact]
    public async Task A_lot_of_a_closed_cycle_is_still_closed_by_hand()
    {
        using var api = new AuthApiFactory();
        var raft = await api.SeededRaftAsync();
        var manager = await api.ManagerAsync(raft.ZoneId);
        var lot = await manager.Client.HarvestLotAsync(raft.RaftId);
        await api.CloseCycleAsync(raft.CycleId);
        var read = await manager.Client.GetHarvestLotAsync(lot.Id());
        Assert.Equal((false, true), (read.GetProperty("canCorrect").GetBoolean(), read.GetProperty("canClose").GetBoolean()));

        var closed = (await api.CloseLotAsync(manager.Client, lot.Id(), 20m, "Unsold")).GetProperty("lot");

        Assert.Equal(("Closed", 20m), (closed.Text("status"), closed.Kg("leftoverKg")));
    }

    // BR-HARVEST-07: the form proposes the remaining kg of a lot already partly sold; the lost kg is the leftover. A lot closed by its sale is no longer for sale.
    [Fact]
    public async Task A_lot_on_a_sale_proposes_its_remaining_kg_and_a_lot_sold_whole_cannot_be_closed_again()
    {
        using var api = new SalesDoubleApiFactory();
        var raft = await api.SeededRaftAsync();
        var owner = raft.Owner.Client;
        var partly = await owner.HarvestLotAsync(raft.RaftId);
        var whole = await owner.HarvestLotAsync(raft.RaftId);
        api.Sales.Figures[partly.Id()] = new LotSalesFigures(300m, 0m, 0m, 0m);
        Assert.Equal(100m, (await owner.GetHarvestLotAsync(partly.Id())).GetProperty("figures").Kg("remainingKg"));

        var figures = (await api.CloseLotAsync(owner, partly.Id(), 100m, "Unsold")).GetProperty("lot").GetProperty("figures");

        // The remaining kg follows its formula whatever the status; the leftover makes it lost, so the difference is 0.
        Assert.Equal((300m, 100m, 0m, 100m), (figures.Kg("soldKg"), figures.Kg("lostKg"), figures.Kg("differenceKg"), figures.Kg("remainingKg")));
        await api.OnLotAsync(whole.Id(), (s, l) => Assert.Null(s.CloseSoldWholeLot(l, FarmingTestKit.Today, Guid.NewGuid())));
        Assert.Equal("MSG-GEN-09", await (await api.SendCloseAsync(owner, whole.Id(), 0m)).ErrorCodeAsync());
    }

    // HARVEST-02.1 (CLAIM-03 extension point): the same checks as the screen, held in the caller's save.
    [Fact]
    public async Task The_sales_return_hook_closes_in_the_callers_save_and_checks_the_zone_and_the_fields()
    {
        using var api = new AuthApiFactory();
        var raft = await api.SeededRaftAsync();
        var manager = await api.ManagerAsync(raft.ZoneId);
        var other = await api.ManagerAsync((await raft.Owner.Client.CreateZoneAsync()).Id());
        var lot = await manager.Client.HarvestLotAsync(raft.RaftId);

        using var scope = api.Services.CreateScope();
        var services = scope.ServiceProvider;
        var tracked = await services.GetRequiredService<IHarvestLotRepository>().GetForEditAsync(lot.Id(), CancellationToken.None);
        var closing = services.GetRequiredService<HarvestLotClosingService>();
        using (services.GetRequiredService<AmbientActor>().Use(other.Account.Id))
        {
            Assert.Equal("MSG-GEN-08", (await closing.CloseWithoutSavingAsync(tracked!, 0m, null, CancellationToken.None))!.Message.Code);
        }

        using var acting = services.GetRequiredService<AmbientActor>().Use(manager.Account.Id);
        Assert.Equal("Close reason", (await closing.CloseWithoutSavingAsync(tracked!, 5m, null, CancellationToken.None))!.Details!["field"]);
        Assert.Null(await closing.CloseWithoutSavingAsync(tracked!, 5m, "Unsold", CancellationToken.None));
        Assert.Equal(StatusIds.HarvestLot.Open, (await api.LotRowAsync(lot.Id())).StatusId);

        await services.GetRequiredService<IUnitOfWork>().SaveChangesAsync();

        var saved = await api.LotRowAsync(lot.Id());
        Assert.Equal((StatusIds.HarvestLot.Closed, 5m, HarvestLotCloseReason.Unsold), (saved.StatusId, saved.LeftoverKg!.Value, saved.CloseReason!.Value));
        Assert.Equal(manager.Account.Id, Assert.Single(await api.CloseRowsAsync(lot.Id())).ActorAccountId);
    }
}
