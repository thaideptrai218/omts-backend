using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using OMTS.Api.IntegrationTests.Infrastructure;
using OMTS.Domain.Constants;
using static OMTS.Api.IntegrationTests.Farming.FarmingTestKit;

namespace OMTS.Api.IntegrationTests.Farming;

/// <summary>Review follow-ups of OMTS-210 that need real row versions.</summary>
public class FarmingReviewFixSqlServerTests
{
    // A zone discontinue and a raft create racing: never a Discontinued zone holding an In Use raft.
    [SqlServerFact]
    public async Task Zone_discontinue_and_raft_create_in_parallel_never_leave_an_in_use_raft_in_a_discontinued_zone()
    {
        await using var host = await FarmingSqlServerHost.StartAsync();
        var owner = host.Owner.Client;
        for (var round = 0; round < 5; round++)
        {
            var zone = await owner.CreateZoneAsync();
            var zoneId = zone.Id();

            await Task.WhenAll(
                owner.PostAsJsonAsync($"/api/zones/{zoneId}/discontinue", new { rowVersion = zone.Text("rowVersion") }),
                owner.PostAsJsonAsync($"/api/zones/{zoneId}/rafts", NewRaftBody()));

            var (zoneStatus, inUseRafts) = await host.QueryAsync(async db => (
                (await db.FarmingZones.SingleAsync(z => z.Id == zoneId)).StatusId,
                await db.Rafts.CountAsync(r => r.ZoneId == zoneId && r.StatusId == StatusIds.Usage.InUse)));
            Assert.False(zoneStatus == StatusIds.Usage.Discontinued && inUseRafts > 0, $"round {round}: discontinued zone with {inUseRafts} rafts in use");
        }
    }
}
