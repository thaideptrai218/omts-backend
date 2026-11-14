using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using OMTS.Api.IntegrationTests.Farming;
using OMTS.Api.IntegrationTests.Infrastructure;
using OMTS.Application.Common;
using OMTS.Domain.Constants;
using OMTS.Infrastructure.Jobs;

namespace OMTS.Api.IntegrationTests.Harvest;

/// <summary>The schedule of the GFR-05 check: 00:05 business time by default (PM default of "the start of the day"), settings read from <c>Jobs:HarvestLotAutoClose</c>, and the catch-up at start.</summary>
public class HarvestLotAutoCloseScheduleTests
{
    private const string RunLine = "Harvest lot daily close check closed";

    [Theory]
    [InlineData("2026-10-29T17:04:00Z", false)]
    [InlineData("2026-10-29T17:05:00Z", true)]
    [InlineData("2026-10-30T09:00:00Z", true)]
    public void The_check_runs_at_00_05_business_time_and_catches_up_by_default(string utc, bool pastRunTime)
    {
        var defaults = new HarvestLotAutoCloseOptions();

        Assert.Equal((new TimeOnly(0, 5), true, true), (defaults.RunAt, defaults.CatchUpOnStart, ((IDailyJobOptions)defaults).Enabled));
        Assert.Equal(pastRunTime, HarvestLotAutoCloseHostedService.IsPastRunTime(DateTimeOffset.Parse(utc), defaults.RunAt));
        // 00:04 local waits one minute.
        Assert.Equal(TimeSpan.FromMinutes(1), HarvestLotAutoCloseHostedService.DelayUntilNextRun(DateTimeOffset.Parse("2026-10-29T17:04:00Z"), defaults.RunAt));
    }

    [Fact]
    public void The_settings_are_read_from_their_section_and_test_hosts_do_not_catch_up()
    {
        using var api = new AuthApiFactory();
        using var moved = api.WithWebHostBuilder(b => b.UseSetting($"{HarvestLotAutoCloseOptions.SectionName}:RunAt", "00:30"));

        var options = moved.Services.GetRequiredService<IOptions<HarvestLotAutoCloseOptions>>().Value;

        Assert.Equal((new TimeOnly(0, 30), false), (options.RunAt, options.CatchUpOnStart));
    }

    // A host started after 00:05 runs the check once at start, for the business date of its clock.
    [Fact]
    public async Task A_host_started_after_the_run_time_closes_the_due_lots_once_at_start()
    {
        using var api = new AuthApiFactory();
        var raft = await api.SeededRaftAsync();
        var lot = await raft.Owner.Client.HarvestLotAsync(raft.RaftId);
        var clock = new FixedClock(new DateTimeOffset(FarmingTestKit.Today.AddDays(4).ToDateTime(new TimeOnly(8, 0)), BusinessClock.UtcOffset));
        var hosted = new HarvestLotAutoCloseHostedService(
            api.Services.GetRequiredService<PendingSaleEndOfDayStopJob>(),
            api.Services.GetRequiredService<HarvestLotAutoCloseJob>(),
            Options.Create(new HarvestLotAutoCloseOptions { CatchUpOnStart = true }),
            clock,
            api.Services.GetRequiredService<ILogger<HarvestLotAutoCloseHostedService>>());

        await hosted.StartAsync(CancellationToken.None);
        try
        {
            var until = DateTime.UtcNow.AddSeconds(30);
            while (!api.Logs.Lines.Any(l => l.Contains(RunLine, StringComparison.Ordinal)))
            {
                Assert.True(DateTime.UtcNow < until, "The catch-up run did not happen within 30 seconds.");
                await Task.Delay(100);
            }
        }
        finally
        {
            await hosted.StopAsync(CancellationToken.None);
        }

        Assert.Contains(api.Logs.Lines, l => l.Contains($"{RunLine} 1 lots and warned 0", StringComparison.Ordinal));
        var closed = await api.LotRowAsync(lot.Id());
        Assert.Equal((StatusIds.HarvestLot.Closed, FarmingTestKit.Today.AddDays(4)), (closed.StatusId, closed.CloseDate!.Value));
    }
}
