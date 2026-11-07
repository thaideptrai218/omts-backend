using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using OMTS.Api.IntegrationTests.Farming;
using OMTS.Api.IntegrationTests.Infrastructure;
using OMTS.Domain.Enums;
using static OMTS.Api.IntegrationTests.Farming.FarmingTestKit;

namespace OMTS.Api.IntegrationTests.Cultivation;

/// <summary>§I.4.4 on every log route, and what a log changes on the cycle: the cancel, the start date and the progress view.</summary>
public class CultivationLogAccessAndCycleTests(AuthApiFactory factory) : IClassFixture<AuthApiFactory>
{
    private static IEnumerable<Func<Task<HttpResponseMessage>>> EveryRoute(HttpClient client, Guid cycleId, Guid logId) =>
    [
        () => client.GetAsync($"/api/cultivation-cycles/{cycleId}/cultivation-logs"),
        () => client.PostLogAsync(cycleId),
        () => client.GetAsync($"/api/cultivation-logs/{logId}"),
        () => client.PutLogAsync(logId, new LogFields { Reason = "no access", RowVersion = AcsTestKit.StaleRowVersion }),
        () => client.RemoveLogAsync(logId, "no access", ""),
    ];

    [Fact]
    public async Task Anonymous_gets_401_everywhere()
    {
        var setup = await factory.CycleAsync();
        var log = await setup.Owner.Client.RecordLogAsync(setup.CycleId);
        var anonymous = factory.CreateClient();

        foreach (var call in EveryRoute(anonymous, setup.CycleId, log.Id()))
        {
            Assert.Equal(HttpStatusCode.Unauthorized, (await call()).StatusCode);
        }
    }

    [Theory]
    [InlineData(StaffRole.Accountant, true)]
    [InlineData(StaffRole.Accountant, false)]
    [InlineData(StaffRole.ZoneManager, false)]
    public async Task An_accountant_and_a_zone_manager_of_another_zone_get_msg_gen_08_everywhere(StaffRole role, bool allZones)
    {
        var setup = await factory.CycleAsync();
        var log = await setup.Owner.Client.RecordLogAsync(setup.CycleId);
        var zone = role == StaffRole.Accountant
            ? (allZones ? null : await factory.ZoneEntityAsync(setup.ZoneId))
            : await factory.ZoneEntityAsync((await setup.Owner.Client.CreateZoneAsync()).Id());
        var refused = await factory.StaffAsync(zone, role);

        foreach (var call in EveryRoute(refused.Client, setup.CycleId, log.Id()))
        {
            var answer = await call();
            Assert.Equal(HttpStatusCode.Forbidden, answer.StatusCode);
            Assert.Equal("MSG-GEN-08", await answer.ErrorCodeAsync());
        }

        Assert.Equal(1, await factory.LogCountAsync(setup.CycleId));
    }

    [Fact]
    public async Task A_zone_manager_of_the_zone_and_of_all_zones_and_the_owner_work()
    {
        var setup = await factory.CycleAsync();
        foreach (var client in new[] { (await factory.ManagerAsync(setup.ZoneId)).Client, (await factory.StaffAsync(null)).Client, setup.Owner.Client })
        {
            var log = await client.RecordLogAsync(setup.CycleId);
            Assert.Equal(HttpStatusCode.OK, (await client.GetAsync($"/api/cultivation-logs/{log.Id()}")).StatusCode);
            Assert.Equal(HttpStatusCode.OK, (await client.GetAsync($"/api/cultivation-cycles/{setup.CycleId}/cultivation-logs")).StatusCode);
            await (await client.PutLogAsync(log.Id(), new LogFields { Reason = "a better photo", RowVersion = await factory.LogVersionAsync(log.Id()) })).ReadAsync(HttpStatusCode.OK);
            await (await client.RemoveLogAsync(log.Id(), "wrong raft", await factory.LogVersionAsync(log.Id()))).ReadAsync(HttpStatusCode.NoContent);
        }
    }

    // MSG-GEN-06 until the last log is removed; the commands say the same on both screens.
    [Fact]
    public async Task A_cycle_with_a_log_cannot_be_cancelled_until_the_log_is_removed()
    {
        var setup = await factory.CycleAsync();
        var owner = setup.Owner.Client;
        var log = await owner.RecordLogAsync(setup.CycleId);

        var cycle = await owner.GetFromJsonAsync<JsonElement>($"/api/cultivation-cycles/{setup.CycleId}");
        Assert.False(cycle.Flag("canCancel"));
        Assert.True(cycle.Flag("canEdit"));
        Assert.False((await owner.ZoneRowAsync(setup.ZoneId, setup.RaftId)).Flag("canCancel"));

        var refused = await (await owner.CancelCycleAsync(setup.CycleId, cycle.Text("rowVersion"))).ReadAsync(HttpStatusCode.Conflict);
        Assert.Equal(("MSG-GEN-06", "MSG11"), (refused.Text("code"), refused.Text("legacyCode")));
        Assert.Equal(setup.CycleId, refused.GetProperty("cycleId").GetGuid());

        await (await owner.RemoveLogAsync(log.Id(), "wrong raft", "")).ReadAsync(HttpStatusCode.NoContent);

        var empty = await owner.GetFromJsonAsync<JsonElement>($"/api/cultivation-cycles/{setup.CycleId}");
        Assert.True(empty.Flag("canCancel"));
        Assert.True((await owner.ZoneRowAsync(setup.ZoneId, setup.RaftId)).Flag("canCancel"));
        Assert.Equal(HttpStatusCode.NoContent, (await owner.CancelCycleAsync(setup.CycleId, empty.Text("rowVersion"))).StatusCode);
    }

    // BR-CYCLE-01: a new start date must not be after the first log, and a log cannot move before the start date.
    [Fact]
    public async Task The_start_date_stays_on_or_before_the_first_log()
    {
        var setup = await factory.CycleAsync();
        var owner = setup.Owner.Client;
        var log = await owner.RecordLogAsync(setup.CycleId, new LogFields { VisitDate = Today.AddDays(-5) });
        await owner.RecordLogAsync(setup.CycleId, new LogFields { VisitDate = Today.AddDays(-2) });
        var cycle = await owner.GetFromJsonAsync<JsonElement>($"/api/cultivation-cycles/{setup.CycleId}");

        var late = await owner.PutCycleAsync(setup.CycleId, new CycleBody(Today.AddDays(-4), RowVersion: cycle.Text("rowVersion")));
        Assert.Equal(["Start date"], await late.InvalidFieldsAsync());

        var moved = await owner.EditCycleAsync(setup.CycleId, new CycleBody(Today.AddDays(-5), RowVersion: cycle.Text("rowVersion")));
        Assert.Equal(Today.AddDays(-5), moved.Date("startDate"));
        var before = new LogFields { VisitDate = Today.AddDays(-6), Reason = "wrong date", RowVersion = await factory.LogVersionAsync(log.Id()), KeepPhotoKeys = log.PhotoKeys(), Photos = [] };
        Assert.Equal(["Visit date"], await (await owner.PutLogAsync(log.Id(), before)).InvalidFieldsAsync());
    }

    [Fact]
    public async Task The_progress_view_lists_the_logs_of_the_cycle_shown()
    {
        var setup = await factory.CycleAsync();
        var manager = await factory.ManagerAsync(setup.ZoneId);
        var earlier = await factory.AddCycleAsync(setup.RaftId, Today.AddDays(-40), Today.AddDays(-30));
        var first = await manager.Client.RecordLogAsync(setup.CycleId, new LogFields { VisitDate = Today.AddDays(-5), Notes = "first" });
        var second = await manager.Client.RecordLogAsync(setup.CycleId, new LogFields { Notes = "second" });

        var today = await manager.Client.ProgressAsync(setup.RaftId);
        var logs = today.GetProperty("logs").EnumerateArray().ToList();
        Assert.Equal([second.Id(), first.Id()], logs.Select(l => l.Id()));
        Assert.True(logs[0].Flag("canCorrect"));
        Assert.False(string.IsNullOrEmpty(logs[0].GetProperty("photos")[0].Text("url")));

        // An earlier date of the same cycle shows the same logs; so the Owner reviews a log whatever the date chosen.
        var earlyInCycle = await manager.Client.ProgressAsync(setup.RaftId, $"?date={Today.AddDays(-9):yyyy-MM-dd}");
        Assert.Equal(2, earlyInCycle.GetProperty("logs").GetArrayLength());

        Assert.Equal(0, (await manager.Client.ProgressAsync(setup.RaftId, $"?cycleId={earlier.Id}")).GetProperty("logs").GetArrayLength());
        Assert.Equal(0, (await manager.Client.ProgressAsync(setup.RaftId, $"?date={Today.AddDays(-20):yyyy-MM-dd}")).GetProperty("logs").GetArrayLength());
    }
}
