using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using OMTS.Api.IntegrationTests.Farming;
using OMTS.Api.IntegrationTests.Infrastructure;
using OMTS.Domain.Constants;
using static OMTS.Api.IntegrationTests.Farming.FarmingTestKit;

namespace OMTS.Api.IntegrationTests.Cultivation;

/// <summary>The API with a clock a test moves after logging in: tokens are checked against the real time, business dates against this clock.</summary>
public sealed class CyclesQaClockApiFactory : AuthApiFactory
{
    public CyclesQaClock Clock { get; } = new();

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);
        builder.ConfigureTestServices(services =>
        {
            services.RemoveAll<TimeProvider>();
            services.AddSingleton<TimeProvider>(Clock);
        });
    }
}

public sealed class CyclesQaClock : TimeProvider
{
    public DateTimeOffset? At { get; set; }

    public override DateTimeOffset GetUtcNow() => At ?? base.GetUtcNow();
}

/// <summary>QA of BR-CYCLE-01 around midnight in Vietnam: 16:59 and 17:01 UTC are different business days, on one UTC day.</summary>
public class CyclesQaTestsMidnight
{
    private static DateTimeOffset MidnightInVietnam(DateOnly day) => new(day.ToDateTime(TimeOnly.MinValue), TimeSpan.FromHours(7));

    private static async Task<(CyclesQaClockApiFactory Api, HttpClient Owner, Guid ZoneId)> ArrangeAsync()
    {
        var api = new CyclesQaClockApiFactory();
        var owner = (await api.OwnerAsync()).Client;
        return (api, owner, (await owner.CreateZoneAsync()).Id());
    }

    [Fact]
    public async Task A_minute_before_midnight_today_is_still_the_old_day()
    {
        var (api, owner, zoneId) = await ArrangeAsync();
        using var host = api;
        var (oldDay, newDay) = (Today.AddDays(-1), Today);
        var (first, second) = ((await owner.CreateRaftAsync(zoneId)).Id(), (await owner.CreateRaftAsync(zoneId)).Id());

        api.Clock.At = MidnightInVietnam(newDay).AddMinutes(-1);

        var started = await owner.StartCycleAsync(first);
        Assert.Equal((oldDay, 1), (started.Date("startDate"), started.GetProperty("daysOfFarming").GetInt32()));
        Assert.Equal(["Start date"], await (await owner.PostCycleAsync(second, new CycleBody(newDay))).InvalidFieldsAsync());
        Assert.Equal(oldDay, (await owner.ProgressAsync(second)).Date("date"));
        Assert.Equal(["Date"], await (await owner.GetAsync($"/api/rafts/{second}/progress?date={newDay:yyyy-MM-dd}")).InvalidFieldsAsync());
        Assert.Equal(oldDay, (await owner.GetFromJsonAsync<JsonElement>($"/api/rafts/{second}/lanes/today")).Date("date"));
    }

    [Fact]
    public async Task A_minute_after_midnight_today_is_the_new_day_though_the_utc_date_is_still_the_old_one()
    {
        var (api, owner, zoneId) = await ArrangeAsync();
        using var host = api;
        var day = Today;
        var (byDefault, onDay, future, closedYesterday, closedToday) = (
            (await owner.CreateRaftAsync(zoneId)).Id(), (await owner.CreateRaftAsync(zoneId)).Id(), (await owner.CreateRaftAsync(zoneId)).Id(),
            (await owner.CreateRaftAsync(zoneId)).Id(), (await owner.CreateRaftAsync(zoneId)).Id());
        await api.AddCycleAsync(closedYesterday, day.AddDays(-9), day.AddDays(-1));
        await api.AddCycleAsync(closedToday, day.AddDays(-9), day);

        api.Clock.At = MidnightInVietnam(day).AddMinutes(1);

        Assert.Equal(day.AddDays(-1), DateOnly.FromDateTime(api.Clock.GetUtcNow().UtcDateTime));
        Assert.Equal(day, (await owner.StartCycleAsync(byDefault)).Date("startDate"));
        Assert.Equal(2, (await owner.StartCycleAsync(onDay, new CycleBody(day.AddDays(-1)))).GetProperty("daysOfFarming").GetInt32());
        Assert.Equal(["Start date"], await (await owner.PostCycleAsync(future, new CycleBody(day.AddDays(1)))).InvalidFieldsAsync());
        Assert.Equal(day, (await owner.StartCycleAsync(closedYesterday)).Date("startDate"));
        Assert.Equal(["Start date"], await (await owner.PostCycleAsync(closedToday)).InvalidFieldsAsync());
        Assert.False((await owner.ZoneRowAsync(zoneId, closedToday)).Flag("canStart"));
    }

    // L4: a lane belongs to its date in Vietnam: added a minute after midnight, it is on the new day and not on the old one, on one UTC date.
    [Fact]
    public async Task A_lane_added_a_minute_after_midnight_in_vietnam_is_not_on_the_day_before()
    {
        var (api, owner, zoneId) = await ArrangeAsync();
        using var host = api;
        var (oldDay, newDay) = (Today.AddDays(-1), Today);

        api.Clock.At = MidnightInVietnam(newDay).AddMinutes(-1);
        var raft = await owner.CreateRaftAsync(zoneId, NewRaftBody(lanes: 2));

        api.Clock.At = MidnightInVietnam(newDay).AddMinutes(1);
        await owner.EditRaftAsync(raft.Id(), NewRaftBody(raft.Text("code"), lanes: 3) with { RowVersion = raft.Text("rowVersion") });

        Assert.Equal(oldDay, DateOnly.FromDateTime(api.Clock.GetUtcNow().UtcDateTime));
        Assert.Equal([1, 2], (await owner.ProgressAsync(raft.Id(), $"?date={oldDay:yyyy-MM-dd}")).LaneNumbers(StatusIds.Lane.InUse));
        Assert.Equal([1, 2, 3], (await owner.ProgressAsync(raft.Id())).LaneNumbers(StatusIds.Lane.InUse));
        Assert.Equal([1, 2, 3], (await owner.GetFromJsonAsync<JsonElement>($"/api/rafts/{raft.Id()}/lanes/today")).LaneNumbers(StatusIds.Lane.InUse));
    }

    // BR-CYCLE-01: a raft closed today can start its next cycle from tomorrow, and "tomorrow" begins at midnight in Vietnam.
    [Fact]
    public async Task A_raft_closed_today_can_start_again_one_minute_after_midnight_in_vietnam()
    {
        var (api, owner, zoneId) = await ArrangeAsync();
        using var host = api;
        var (closeDay, nextDay) = (Today.AddDays(-1), Today);
        var raftId = (await owner.CreateRaftAsync(zoneId)).Id();
        await api.AddCycleAsync(raftId, closeDay.AddDays(-5), closeDay);

        api.Clock.At = MidnightInVietnam(nextDay).AddMinutes(-1);
        Assert.False((await owner.ZoneRowAsync(zoneId, raftId)).Flag("canStart"));
        Assert.Equal(["Start date"], await (await owner.PostCycleAsync(raftId)).InvalidFieldsAsync());
        Assert.Equal(["Start date"], await (await owner.PostCycleAsync(raftId, new CycleBody(nextDay))).InvalidFieldsAsync());

        api.Clock.At = MidnightInVietnam(nextDay).AddMinutes(1);
        Assert.True((await owner.ZoneRowAsync(zoneId, raftId)).Flag("canStart"));
        var answer = await (await owner.PostCycleAsync(raftId)).ReadAsync(HttpStatusCode.Created);
        Assert.Equal((nextDay, closeDay), (answer.GetProperty("cycle").Date("startDate"), answer.Date("previousCloseDate")));
    }
}
