using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using OMTS.Api.IntegrationTests.Cultivation;
using OMTS.Api.IntegrationTests.Farming;
using OMTS.Api.IntegrationTests.Infrastructure;
using OMTS.Domain.Constants;
using OMTS.Domain.Enums;
using OMTS.Infrastructure.Jobs;
using static OMTS.Api.IntegrationTests.Harvest.HarvestLotCorrectionTestKit;
using static OMTS.Api.IntegrationTests.Sales.SalesInvoiceTestKit;

namespace OMTS.Api.IntegrationTests.Harvest;

/// <summary>QA of PR H2, GFR-05: the business day on a UTC server, who is warned, a lot sold meanwhile, and the HARVEST-03 count of "the next daily close check".</summary>
public class HarvestLotCloseQaTestsAutoClose
{
    private static readonly DateOnly Today = FarmingTestKit.Today;

    private static DateTimeOffset LocalTime(int month, int day, int hour, int minute) => new(2026, month, day, hour, minute, 0, TimeSpan.FromHours(7));

    // GFR-05 "at the start of the day" in Vietnam (UTC+7, no DST) on a server that runs in UTC: the hosted check at 17:05Z on 29/10 is the check of 30/10. A lot harvested at 23:30 on 26/10
    // (16:30Z) is due; one harvested at 00:30 on 27/10 (17:30Z on 26/10, the same UTC day) is only warned for 31/10.
    [Fact]
    public async Task The_hosted_check_at_00_05_in_vietnam_closes_by_the_local_harvest_date_not_the_utc_one()
    {
        using var api = new CyclesQaClockApiFactory();
        var (owner, _, raftId, _) = await api.ClockedRaftAsync();
        api.Clock.At = LocalTime(10, 26, 23, 30);
        var late = await owner.HarvestLotAsync(raftId, HarvestFields.OfLanes(1));
        api.Clock.At = LocalTime(10, 27, 0, 30);
        var early = await owner.HarvestLotAsync(raftId, HarvestFields.OfLanes(2));
        Assert.Equal(("KV01-B01-20261026", "KV01-B01-20261027"), (late.Text("lotCode"), early.Text("lotCode")));

        var runAt = new DateTimeOffset(2026, 10, 29, 17, 5, 0, TimeSpan.Zero);
        api.Clock.At = runAt;
        var hosted = new HarvestLotAutoCloseHostedService(
            api.Services.GetRequiredService<PendingSaleEndOfDayStopJob>(),
            api.Services.GetRequiredService<HarvestLotAutoCloseJob>(),
            Options.Create(new HarvestLotAutoCloseOptions { CatchUpOnStart = true }),
            new FixedClock(runAt),
            api.Services.GetRequiredService<ILogger<HarvestLotAutoCloseHostedService>>());
        await hosted.StartAsync(CancellationToken.None);
        try
        {
            var until = DateTime.UtcNow.AddSeconds(30);
            while (!api.Logs.Lines.Any(l => l.Contains("Harvest lot daily close check closed", StringComparison.Ordinal)))
            {
                Assert.True(DateTime.UtcNow < until, "The catch-up run did not happen within 30 seconds.");
                await Task.Delay(100);
            }
        }
        finally
        {
            await hosted.StopAsync(CancellationToken.None);
        }

        var closed = await api.LotRowAsync(late.Id());
        Assert.Equal((StatusIds.HarvestLot.Closed, new DateOnly(2026, 10, 30)), (closed.StatusId, closed.CloseDate!.Value));
        Assert.Equal(StatusIds.HarvestLot.Open, (await api.LotRowAsync(early.Id())).StatusId);
        var warning = Assert.Single(await api.WarningsAsync(early.Id()));
        Assert.Equal("2026-10-31", JsonDocument.Parse(warning.MessageParams!).RootElement.Text("close_date"));
    }

    // GFR-05 / BR-FARM-08: "the Zone Managers of the zone" are its own and the All-zones ones; the Owner (the zone has managers) and a Zone Manager of another zone are not told.
    [Fact]
    public async Task The_warning_goes_to_the_zone_and_all_zones_managers_only()
    {
        using var api = new AuthApiFactory();
        var raft = await api.SeededRaftAsync();
        var manager = await api.ManagerAsync(raft.ZoneId);
        var allZones = await api.StaffAsync(null);
        await api.ManagerAsync((await raft.Owner.Client.CreateZoneAsync()).Id());
        var lot = await manager.Client.HarvestLotAsync(raft.RaftId);

        Assert.Equal(new HarvestLotAutoCloseRun(0, 1), await api.AutoCloseOnAsync(Today.AddDays(3)));

        var told = (await api.WarningsAsync(lot.Id())).Select(n => n.RecipientAccountId!.Value).Order().ToList();
        Assert.Equal(new[] { manager.Account.Id, allZones.Account.Id }.Order().ToList(), told);
    }

    // A lot sold whole before its close day is no longer for sale: the check leaves it with its sale's close, and closes only the lot still for sale.
    [Fact]
    public async Task A_lot_sold_whole_after_its_warning_is_left_to_its_sale_and_only_the_other_lot_is_closed()
    {
        using var api = new AuthApiFactory();
        var sale = await api.SaleSetupAsync();
        Assert.Equal(new HarvestLotAutoCloseRun(0, 2), await api.AutoCloseOnAsync(Today.AddDays(3)));

        await sale.Manager.Client.SellAsync(Invoice(sale.CustomerId, [Line(sale.ProductId, sale.LotId, 50m)], Cash(1_500_000m)));

        Assert.Equal(new HarvestLotAutoCloseRun(1, 0), await api.AutoCloseOnAsync(Today.AddDays(4)));
        var sold = await api.LotRowAsync(sale.LotId);
        Assert.Equal((HarvestLotCloseReason.SoldWholeLot, Today), (sold.CloseReason!.Value, sold.CloseDate!.Value));
        Assert.Single(await api.CloseRowsAsync(sale.LotId));
        Assert.Equal(HarvestLotCloseReason.TimeLimitReached, (await api.LotRowAsync(sale.OtherLotId)).CloseReason);
    }

    // HARVEST-03 step 4 / MSG-HARVEST-03: "how many lots for sale the next daily close check will close". At 00:02 the next check is today's at 00:05, not tomorrow's, so a lot it only
    // warns must not be counted: the Owner is told 1 lot will be closed, and the check closes none.
    [Fact]
    public async Task Before_todays_check_runs_the_lowered_days_count_the_lots_that_check_closes()
    {
        using var api = new CyclesQaClockApiFactory();
        var (owner, _, raftId, _) = await api.ClockedRaftAsync();
        await owner.SetHarvestSettingsAsync(50m, 5);
        api.Clock.At = InVietnam(10, 27);
        var lot = await owner.HarvestLotAsync(raftId, HarvestFields.OfLanes(1));
        api.Clock.At = LocalTime(10, 30, 0, 2);

        var preview = await owner.GetFromJsonAsync<JsonElement>("/api/harvest-settings/auto-close-preview?days=3");
        await owner.SetHarvestSettingsAsync(50m, 3);
        var run = await api.AutoCloseOnAsync(new DateOnly(2026, 10, 30));

        Assert.Equal(new HarvestLotAutoCloseRun(0, 1), run);
        Assert.Equal(StatusIds.HarvestLot.Open, (await api.LotRowAsync(lot.Id())).StatusId);
        Assert.Equal(run.Closed, preview.GetProperty("lotCount").GetInt32());
    }
}
