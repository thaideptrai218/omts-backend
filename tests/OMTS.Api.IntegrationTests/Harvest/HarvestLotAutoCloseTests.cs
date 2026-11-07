using System.Text.Json;
using OMTS.Api.IntegrationTests.Cultivation;
using OMTS.Api.IntegrationTests.Farming;
using OMTS.Api.IntegrationTests.Infrastructure;
using OMTS.Application.Features.Harvest;
using OMTS.Domain.Constants;
using OMTS.Domain.Enums;
using OMTS.Infrastructure.Jobs;
using static OMTS.Api.IntegrationTests.Harvest.HarvestLotCorrectionTestKit;

namespace OMTS.Api.IntegrationTests.Harvest;

/// <summary>GFR-05 through the job's single run: the example of the SRS, Reopened lots, the warning once per close date, a catch-up after downtime, and who is told.</summary>
public class HarvestLotAutoCloseTests
{
    private static readonly DateOnly Today = FarmingTestKit.Today;

    private static async Task<Actor> ManagerOnTheRealClockAsync(CyclesQaClockApiFactory api, Guid zoneId)
    {
        var at = api.Clock.At;
        api.Clock.At = null;
        var manager = await api.ManagerAsync(zoneId);
        api.Clock.At = at;
        return manager;
    }

    // III.13 GFR-05: "with 3 days, a lot harvested on 26/10 can still be sold until the end of 29/10 and is closed at the check at the start of 30/10".
    [Fact]
    public async Task The_srs_example_is_warned_once_on_29_10_and_closed_on_30_10_by_the_system_with_the_remaining_kg()
    {
        using var api = new CyclesQaClockApiFactory();
        var (owner, zoneId, raftId, _) = await api.ClockedRaftAsync();
        var manager = await ManagerOnTheRealClockAsync(api, zoneId);
        api.Clock.At = InVietnam(10, 26);
        var lot = await owner.HarvestLotAsync(raftId, HarvestFields.OfLanes(1));
        Assert.Equal("KV01-B01-20261026", lot.Text("lotCode"));

        Assert.Equal(new HarvestLotAutoCloseRun(0, 0), await api.AutoCloseOnAsync(new DateOnly(2026, 10, 28)));
        Assert.Equal(new HarvestLotAutoCloseRun(0, 1), await api.AutoCloseOnAsync(new DateOnly(2026, 10, 29)));
        Assert.Equal(new HarvestLotAutoCloseRun(0, 0), await api.AutoCloseOnAsync(new DateOnly(2026, 10, 29)));

        // To the Zone Manager of the zone, not the Owner, once.
        var warning = Assert.Single(await api.WarningsAsync(lot.Id()));
        Assert.Equal((manager.Account.Id, "MSG-HARVEST-05", false), (warning.RecipientAccountId, warning.MessageCode, warning.IsMandatory));
        Assert.Equal("Lot KV01-B01-20261026 (raft KV01-B01) will be closed automatically at the start of 30/10/2026. Please sell it before then or close it yourself.", warning.Body);
        Assert.Equal("""{"lot_code":"KV01-B01-20261026","raft_code":"KV01-B01","close_date":"2026-10-30"}""", warning.MessageParams);
        Assert.Equal(StatusIds.HarvestLot.Open, (await api.LotRowAsync(lot.Id())).StatusId);

        Assert.Equal(new HarvestLotAutoCloseRun(1, 0), await api.AutoCloseOnAsync(new DateOnly(2026, 10, 30)));
        Assert.Equal(new HarvestLotAutoCloseRun(0, 0), await api.AutoCloseOnAsync(new DateOnly(2026, 10, 30)));
        Assert.Equal(new HarvestLotAutoCloseRun(0, 0), await api.AutoCloseOnAsync(new DateOnly(2026, 10, 31)));

        var closed = await api.LotRowAsync(lot.Id());
        Assert.Equal((StatusIds.HarvestLot.Closed, new DateOnly(2026, 10, 30), HarvestLotCloseReason.TimeLimitReached, 400m),
            (closed.StatusId, closed.CloseDate!.Value, closed.CloseReason!.Value, closed.LeftoverKg!.Value));
        var row = Assert.Single(await api.CloseRowsAsync(lot.Id()));
        Assert.Null(row.ActorAccountId);
        Assert.Null(row.ActorEmployeeId);
        Assert.Equal((zoneId, "KV01-B01-20261026"), (row.ZoneId!.Value, row.RecordCode));
        Assert.Single(await api.WarningsAsync(lot.Id()));
    }

    // BR-HARVEST-04: a Reopened lot counts again from its reopen day and is warned again for its new close date; the leftover is the remaining kg of BR-HARVEST-07.
    [Fact]
    public async Task A_reopened_lot_counts_from_its_reopen_day_and_closes_with_its_remaining_kg()
    {
        using var api = new SalesDoubleApiFactory();
        var raft = await api.SeededRaftAsync();
        var lot = await raft.Owner.Client.HarvestLotAsync(raft.RaftId);
        Assert.Equal(new HarvestLotAutoCloseRun(0, 1), await api.AutoCloseOnAsync(Today.AddDays(3)));
        await api.CloseLotAsync(raft.Owner.Client, lot.Id(), 0m);
        var reopenedOn = Today.AddDays(10);
        await api.ReopenAsync(lot.Id(), reopenedOn);
        api.Sales.Figures[lot.Id()] = new LotSalesFigures(150m, 50m, 50m, 0m);

        // The harvest date is long past, but the days count from the reopen day.
        Assert.Equal(new HarvestLotAutoCloseRun(0, 0), await api.AutoCloseOnAsync(reopenedOn.AddDays(2)));
        Assert.Equal(new HarvestLotAutoCloseRun(0, 1), await api.AutoCloseOnAsync(reopenedOn.AddDays(3)));
        Assert.Equal(new HarvestLotAutoCloseRun(1, 0), await api.AutoCloseOnAsync(reopenedOn.AddDays(4)));

        var closed = await api.LotRowAsync(lot.Id());
        Assert.Equal((reopenedOn.AddDays(4), 300m, HarvestLotCloseReason.TimeLimitReached), (closed.CloseDate!.Value, closed.LeftoverKg!.Value, closed.CloseReason!.Value));
        var warnings = await api.WarningsAsync(lot.Id());
        Assert.Equal(2, warnings.Count);
        Assert.Equal(
            [Today.AddDays(4).ToString("yyyy-MM-dd"), reopenedOn.AddDays(4).ToString("yyyy-MM-dd")],
            warnings.Select(w => JsonDocument.Parse(w.MessageParams!).RootElement.Text("close_date")).Order());
        Assert.Equal(2, (await api.CloseRowsAsync(lot.Id())).Count);
    }

    // A host down across the checks: the next run closes the overdue lots with the run day as the close date, warns none of them, and a second run changes nothing.
    [Fact]
    public async Task A_catch_up_after_downtime_closes_the_overdue_lots_once_and_leaves_lots_closed_by_hand()
    {
        using var api = new AuthApiFactory();
        var raft = await api.SeededRaftAsync();
        var owner = raft.Owner.Client;
        var overdue = await owner.HarvestLotAsync(raft.RaftId);
        var byHand = await owner.HarvestLotAsync(raft.RaftId);
        await api.CloseLotAsync(owner, byHand.Id(), 7m, "Spoiled");

        Assert.Equal(new HarvestLotAutoCloseRun(1, 0), await api.AutoCloseOnAsync(Today.AddDays(6)));
        Assert.Equal(new HarvestLotAutoCloseRun(0, 0), await api.AutoCloseOnAsync(Today.AddDays(6)));

        Assert.Equal(Today.AddDays(6), (await api.LotRowAsync(overdue.Id())).CloseDate);
        Assert.Empty(await api.WarningsAsync(overdue.Id()));
        var untouched = await api.LotRowAsync(byHand.Id());
        Assert.Equal((Today, 7m, HarvestLotCloseReason.Spoiled), (untouched.CloseDate!.Value, untouched.LeftoverKg!.Value, untouched.CloseReason!.Value));
        Assert.Single(await api.CloseRowsAsync(byHand.Id()));
    }

    // GFR-05: the Owner is told when the zone has no Zone Manager. BR-HARVEST-04 end: a lot of a Closed cycle is still for sale, so it is closed too.
    [Fact]
    public async Task With_no_zone_manager_the_owner_is_warned_and_a_lot_of_a_closed_cycle_is_closed_too()
    {
        using var api = new AuthApiFactory();
        var raft = await api.SeededRaftAsync();
        var lot = await raft.Owner.Client.HarvestLotAsync(raft.RaftId);
        await api.CloseCycleAsync(raft.CycleId);

        Assert.Equal(new HarvestLotAutoCloseRun(0, 1), await api.AutoCloseOnAsync(Today.AddDays(3)));
        Assert.Equal(raft.Owner.Account.Id, Assert.Single(await api.WarningsAsync(lot.Id())).RecipientAccountId);

        Assert.Equal(new HarvestLotAutoCloseRun(1, 0), await api.AutoCloseOnAsync(Today.AddDays(4)));
        Assert.Equal(StatusIds.HarvestLot.Closed, (await api.LotRowAsync(lot.Id())).StatusId);
    }

    // BR-HARVEST-06: new days apply from the next check. Raised after a warning: the new close date is warned once more. Lowered: the next check closes what is due.
    [Fact]
    public async Task New_days_apply_from_the_next_check_and_a_new_close_date_is_warned_once_more()
    {
        using var api = new AuthApiFactory();
        var raft = await api.SeededRaftAsync();
        var owner = raft.Owner.Client;
        var lot = await owner.HarvestLotAsync(raft.RaftId);
        Assert.Equal(new HarvestLotAutoCloseRun(0, 1), await api.AutoCloseOnAsync(Today.AddDays(3)));

        await owner.SetHarvestSettingsAsync(50m, 5);
        Assert.Equal(new HarvestLotAutoCloseRun(0, 0), await api.AutoCloseOnAsync(Today.AddDays(4)));
        Assert.Equal(new HarvestLotAutoCloseRun(0, 1), await api.AutoCloseOnAsync(Today.AddDays(5)));
        Assert.Equal(2, (await api.WarningsAsync(lot.Id())).Count);

        await owner.SetHarvestSettingsAsync(50m, 1);
        Assert.Equal(new HarvestLotAutoCloseRun(1, 0), await api.AutoCloseOnAsync(Today.AddDays(5)));
        Assert.Equal(Today.AddDays(5), (await api.LotRowAsync(lot.Id())).CloseDate);
    }

    // Opening the warning shows the lot (GFR-02 checks the access again).
    [Fact]
    public async Task The_warning_opens_the_lot_for_its_zone_manager()
    {
        using var api = new AuthApiFactory();
        var raft = await api.SeededRaftAsync();
        var manager = await api.ManagerAsync(raft.ZoneId);
        var lot = await manager.Client.HarvestLotAsync(raft.RaftId);
        await api.AutoCloseOnAsync(Today.AddDays(3));
        var warning = Assert.Single(await api.WarningsAsync(lot.Id()));

        var opened = await (await manager.Client.GetAsync($"/api/me/notifications/{warning.Id}/open")).ReadAsync(System.Net.HttpStatusCode.OK);

        Assert.Contains(lot.Id().ToString(), opened.GetRawText(), StringComparison.OrdinalIgnoreCase);
    }
}
