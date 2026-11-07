using System.Net;
using Microsoft.EntityFrameworkCore;
using OMTS.Api.IntegrationTests.Cultivation;
using OMTS.Api.IntegrationTests.Farming;
using OMTS.Api.IntegrationTests.Infrastructure;
using OMTS.Application.Features.Harvest;
using OMTS.Domain.Constants;
using OMTS.Infrastructure.Jobs;
using static OMTS.Api.IntegrationTests.Farming.FarmingTestKit;
using static OMTS.Api.IntegrationTests.Harvest.HarvestLotCloseTestKit;

namespace OMTS.Api.IntegrationTests.Harvest;

/// <summary>
/// GFR-05 and HARVEST-02 on SQL Server, with real row versions: parallel runs of the check (two app instances) close and warn each lot once, a close by hand against the check closes
/// the lot once, and the warning's marker makes an open close form stale.
/// </summary>
public class HarvestLotAutoCloseSqlServerTests
{
    private const int Rounds = 4;

    private static async Task<(FarmingSqlServerHost Host, Guid ZoneId, Actor Manager)> StartAsync()
    {
        var host = await FarmingSqlServerHost.StartAsync();
        var zoneId = (await host.Owner.Client.CreateZoneAsync()).Id();
        return (host, zoneId, await host.StaffInZoneAsync(zoneId));
    }

    /// <summary>Lots harvested today on new rafts of the zone, by the Zone Manager.</summary>
    private static async Task<List<Guid>> LotsAsync(FarmingSqlServerHost host, Guid zoneId, Actor manager, int rafts)
    {
        var owner = host.Owner.Client;
        var ids = new List<Guid>();
        for (var r = 0; r < rafts; r++)
        {
            var raftId = (await owner.CreateRaftAsync(zoneId, NewRaftBody())).Id();
            var cycleId = (await owner.StartCycleAsync(raftId, new CycleBody(Today.AddDays(-10)))).Id();
            await owner.SeedingRecordAsync(cycleId, (await owner.SeedLotAsync()).Session([1, 2, 3, 4, 5]));
            ids.Add((await manager.Client.HarvestLotAsync(raftId, HarvestFields.OfLanes(1))).Id());
            ids.Add((await manager.Client.HarvestLotAsync(raftId, HarvestFields.OfLanes(2))).Id());
        }

        return ids;
    }

    private static async Task<HarvestLotAutoCloseRun[]> ParallelRunsAsync(FarmingSqlServerHost host, DateOnly day) =>
        await Task.WhenAll(Enumerable.Range(0, 3).Select(_ => Task.Run(() => host.Api.AutoCloseOnAsync(day))));

    [SqlServerFact]
    public async Task Parallel_runs_warn_each_lot_once_and_close_each_lot_once()
    {
        var (host, zoneId, manager) = await StartAsync();
        await using var _ = host;
        var lots = await LotsAsync(host, zoneId, manager, rafts: 4);

        var warns = await ParallelRunsAsync(host, Today.AddDays(3));
        var closes = await ParallelRunsAsync(host, Today.AddDays(4));

        Assert.Equal((lots.Count, 0), (warns.Sum(r => r.Warned), warns.Sum(r => r.Closed)));
        Assert.Equal((lots.Count, 0), (closes.Sum(r => r.Closed), closes.Sum(r => r.Warned)));
        foreach (var lotId in lots)
        {
            var warnings = await host.QueryAsync(db => db.Notifications.Where(n => n.Type == WarningType && n.RelatedRecordId == lotId).Select(n => n.RecipientAccountId).ToListAsync());
            Assert.Equal([manager.Account.Id], warnings);
            Assert.Equal(1, await host.QueryAsync(db => db.AuditEvents.CountAsync(e => e.RecordId == lotId && e.Action == HarvestLotAudit.Closed && e.ActorAccountId == null)));
            var lot = await host.QueryAsync(db => db.HarvestLots.AsNoTracking().SingleAsync(l => l.Id == lotId));
            Assert.Equal((StatusIds.HarvestLot.Closed, Today.AddDays(4), Today.AddDays(4)), (lot.StatusId, lot.CloseDate!.Value, lot.AutoCloseWarnedFor!.Value));
        }

        Assert.DoesNotContain(host.Api.Logs.Lines, l => l.Contains("Daily close check failed", StringComparison.Ordinal));
    }

    // A Zone Manager closes the lot as the check closes it: the row version lets one of them through, the other answers 409 or finds it closed.
    [SqlServerFact]
    public async Task A_close_by_hand_against_the_check_closes_the_lot_once_and_answers_200_or_409()
    {
        var (host, zoneId, manager) = await StartAsync();
        await using var _ = host;
        for (var round = 0; round < Rounds; round++)
        {
            // New lots each round: the check closes every lot that is due.
            var lotId = (await LotsAsync(host, zoneId, manager, rafts: 1))[0];
            var version = (await manager.Client.GetHarvestLotAsync(lotId)).Text("rowVersion");

            var byHand = manager.Client.PostCloseLotAsync(lotId, 4m, "Unsold", version);
            var check = Task.Run(() => host.Api.AutoCloseOnAsync(Today.AddDays(4)));
            await Task.WhenAll(byHand, check);

            var answer = await byHand;
            Assert.True(answer.StatusCode is HttpStatusCode.OK or HttpStatusCode.Conflict, $"{(int)answer.StatusCode}: {await answer.Content.ReadAsStringAsync()}");
            Assert.Equal(1, await host.QueryAsync(db => db.AuditEvents.CountAsync(e => e.RecordId == lotId && e.Action == HarvestLotAudit.Closed)));
            var lot = await host.QueryAsync(db => db.HarvestLots.AsNoTracking().SingleAsync(l => l.Id == lotId));
            Assert.Equal(StatusIds.HarvestLot.Closed, lot.StatusId);
            Assert.Equal(answer.StatusCode == HttpStatusCode.OK ? 4m : 400m, lot.LeftoverKg);
        }
    }

    // NFR-09: the warning saves its marker on the lot, so a close form read before it is stale; read again, it closes.
    [SqlServerFact]
    public async Task A_close_form_read_before_the_warning_answers_msg06_and_read_again_closes()
    {
        var (host, zoneId, manager) = await StartAsync();
        await using var _ = host;
        var lotId = (await LotsAsync(host, zoneId, manager, rafts: 1))[0];
        var version = (await manager.Client.GetHarvestLotAsync(lotId)).Text("rowVersion");
        Assert.True((await host.Api.AutoCloseOnAsync(Today.AddDays(3))).Warned >= 1);

        Assert.Equal("MSG06", await (await manager.Client.PostCloseLotAsync(lotId, 0m, null, version)).ErrorCodeAsync());

        var fresh = (await manager.Client.GetHarvestLotAsync(lotId)).Text("rowVersion");
        Assert.Equal(HttpStatusCode.OK, (await manager.Client.PostCloseLotAsync(lotId, 0m, null, fresh)).StatusCode);
    }
}
